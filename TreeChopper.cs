using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;

namespace FarmAssistant;

/// <summary>
/// 自动砍伐：手持斧头按快捷键，找出离自己最近的那一片（配置里可改成整张地图）该砍的目标——长成的野树、
/// 树桩、地上的木垛，以及斧头等级够的大树桩 / 大木头——按一开始排好的顺序一个个砍完。
/// 不砍：树苗和小树（多半是玩家自己种的）、果树、装了采集器的树、镇上路边的树。
///
/// 每一斧都和玩家按工具键相同：面朝目标、开始使用工具（斧头不蓄力，游戏起手后自己挥出），
/// 伤害、体力、掉落全部由游戏结算。
///
/// <list type="bullet">
/// <item>顺序：开始时一次排好——从自己出发每次接最近的那个，再用 2-opt（把交叉的两段反过来接）拉直。
///   别人中途砍掉的目标直接跳过。</item>
/// <item>倒向：最后一斧时脚在树干中线右边，树就往左倒，否则往右倒；那堆木头落在倒向那侧 4 格处。
///   每棵树往"顺序里的下一个"那侧倒（避开水、出界、吸不到的地方）。上下方的站位靠脚的左右微调决定倒向。</item>
/// <item>每棵树：砍倒 → 不等倒树动画，原站位接着砍树桩（倒树途中砍掉树桩，游戏会让树留到倒完、照常生成木头，
///   Tree.performTreeFall 在 falling 时返回 false）→ 把这棵树的掉落物捡干净 → 去下一个。不留到后面。</item>
/// <item>捡：木头还没生成（树还在倒）就先走到预判的那堆旁边等着；生成后按每件掉落物预判的停稳位置选站位捡光。
///   吸附范围用角色当前的磁力半径（戴磁铁戒指会更大）。</item>
/// </list>
/// </summary>
internal sealed class TreeChopper : AutoTask
{
    private enum Phase { Idle, Decide, Walk, Start, Swing, PileWalk, PileWait, Gather, FinalSweep }

    private sealed class Target
    {
        public Tree? Tree;
        public ResourceClump? Clump;
        public StardewValley.Object? Twig;      // 地上的木垛（树枝），一斧就碎
        public Point Tile;               // 树所在格 / 大树桩左上角
        public string What = "";
    }

    private const int FallDistance = 4;     // 倒下后木头落在树干旁 4 格
    private const float FootShift = 10f;    // 站在上下方时，脚偏离中线多少像素来选倒向
    private const int GatherRange = 6;      // 捡一棵树的掉落物：树干与木头堆中点周围这么多格
    private const int MaxPileWait = 240;    // 等木头生成最多 4 秒（倒树动画约 2.1 秒）
    private const int ClusterGap = 4;       // 只砍一片时：相距 ≤ 4 格的目标算同一片

    /// <summary>那堆木头 4 件的平均停稳位置（相对抛出点，像素；按游戏的速度范围和弹跳规则模拟得出）。</summary>
    private static readonly Vector2[] PileOffsets = { new(-75f, 0f), new(75f, 0f), new(0f, -95f), new(0f, 66f) };

    private readonly DropCollector collector = new();
    private readonly List<Target> order = new();
    private int nextIndex;
    private Phase phase = Phase.Idle;
    private TileGrid grid = null!;
    private Target? current;
    private bool hitCurrent;                // 自己砍过这个目标（别人砍掉的就不算、不去捡）
    private Tree? fallingTree;              // 正在倒的那棵（木头还没生成）
    private Vector2? pileBase;              // 那堆木头的抛出点（游戏判断吸附用的坐标，已含 +32）
    private Rectangle area;                 // 所有目标的外接框（最后兜底扫一遍用）
    private Point stand;
    private int dir;
    private float footOffset;
    private int swings, noProgress, done, unreachable;
    private float healthBefore;

    public TreeChopper(ModEntry mod) : base(mod) { }

    public override bool Active => phase != Phase.Idle;
    protected override string Name => "自动砍伐";
    protected override string Icon => "(T)Axe";

    // 最后兜底扫掉落物时手里拿什么都行。
    protected override string? HeldProblem(Farmer player) =>
        phase == Phase.FinalSweep || player.CurrentTool is Axe ? null : "换下了斧头，已停止自动砍伐";

    protected override void OnStop()
    {
        phase = Phase.Idle;
        order.Clear();
        current = null;
        collector.Stop();
    }

    // ---------- 开始：扫描目标、排顺序 ----------

    /// <summary>
    /// 扫描该砍的目标、排好顺序并开始；没有可砍的返回 false（已提示原因）。
    /// wholeMap = false 时只砍离自己最近（searchRange 格内）的那一片：相距 ≤ ClusterGap 格的目标算连着。
    /// </summary>
    public bool StartAll(GameLocation loc, Axe axe, bool wholeMap, int searchRange)
    {
        var targets = new List<Target>();
        var weakAxe = new List<Rectangle>();      // 斧头等级不够的大树桩 / 大木头
        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            if (pair.Value is not Tree tree || tree.tapped.Value)
                continue;
            var tile = new Point((int)pair.Key.X, (int)pair.Key.Y);
            if (!tree.stump.Value && tree.growthStage.Value < 5)
                continue;                  // 树苗、小树
            // 镇上路边的树：游戏只会弹出警告，不会受伤。
            if (loc is Town && tile.X < 100 && loc.getTileIndexAt(tile.X, tile.Y, "Paths") is 9 or 10 or 11)
                continue;
            targets.Add(new Target { Tree = tree, Tile = tile, What = tree.stump.Value ? "树桩" : "树" });
        }
        foreach (var pair in loc.objects.Pairs)
        {
            if (!pair.Value.IsTwig())
                continue;
            targets.Add(new Target { Twig = pair.Value, Tile = new Point((int)pair.Key.X, (int)pair.Key.Y), What = "木垛" });
        }
        foreach (var rc in loc.resourceClumps)
        {
            int index = rc.parentSheetIndex.Value;
            if (index != ResourceClump.stumpIndex && index != ResourceClump.hollowLogIndex)
                continue;                  // 石头类用镐子，不归斧头管
            if (axe.UpgradeLevel < (index == ResourceClump.stumpIndex ? 1 : 2))
            {
                weakAxe.Add(new Rectangle((int)rc.Tile.X, (int)rc.Tile.Y, rc.width.Value, rc.height.Value));
                continue;
            }
            targets.Add(new Target
            {
                Clump = rc,
                Tile = new Point((int)rc.Tile.X, (int)rc.Tile.Y),
                What = index == ResourceClump.stumpIndex ? "大树桩" : "大木头",
            });
        }

        string where = wholeMap ? "这张地图上" : "附近";
        if (!wholeMap)
            targets = NearestGrove(targets, Game1.player.TilePoint, searchRange);
        // 斧头等级不够的：整图模式全算；一片模式只算挨着这片的。
        int needBetterAxe = wholeMap ? weakAxe.Count
            : weakAxe.Count(r => targets.Exists(t => Gap(Bounds(t), r) <= ClusterGap));
        string axeNote = needBetterAxe > 0 ? $"（{needBetterAxe} 个大树桩 / 大木头斧头等级不够，跳过）" : "";
        if (targets.Count == 0)
        {
            Notify($"{where}没有可砍的树" + axeNote);
            return false;
        }
        int trees = targets.Count(t => t.Tree is { stump.Value: false });
        int stumps = targets.Count(t => t.Tree is { stump.Value: true });
        int clumps = targets.Count(t => t.Clump != null);
        int twigs = targets.Count(t => t.Twig != null);

        Begin(loc);
        grid = new TileGrid(new GameFieldView(loc));
        order.Clear();
        order.AddRange(PlanOrder(Game1.player.TilePoint, targets));
        nextIndex = 0;
        current = null;
        fallingTree = null;
        pileBase = null;
        collector.Reset();
        int x0 = targets.Min(t => t.Tile.X), y0 = targets.Min(t => t.Tile.Y);
        int x1 = targets.Max(t => t.Tile.X), y1 = targets.Max(t => t.Tile.Y);
        area = new Rectangle(x0, y0, x1 - x0 + 2, y1 - y0 + 2);
        swings = noProgress = done = unreachable = 0;
        Enter(Phase.Decide);
        var parts = new List<string>();
        if (trees > 0) parts.Add($"树 {trees}");
        if (stumps > 0) parts.Add($"树桩 {stumps}");
        if (clumps > 0) parts.Add($"大树桩 / 大木头 {clumps}");
        if (twigs > 0) parts.Add($"木垛 {twigs}");
        Notify($"开始自动砍伐{(wholeMap ? "（整张地图）" : "这一片")}：共 {targets.Count} 个（{string.Join("，", parts)}）{axeNote}");
        return true;
    }

    /// <summary>
    /// 离 from 最近（曼哈顿距离 ≤ maxDistance）的那个目标所在的一片：从它出发，
    /// 相距 ≤ ClusterGap 格（按占地矩形之间的格距）的目标算连着，一直蔓延。几百个目标也是微秒级。
    /// </summary>
    private static List<Target> NearestGrove(List<Target> all, Point from, int maxDistance)
    {
        int seed = -1, seedDistance = maxDistance + 1;
        for (int i = 0; i < all.Count; i++)
        {
            int d = Gap(Bounds(all[i]), new Rectangle(from.X, from.Y, 1, 1));
            if (Manhattan(from, all[i].Tile) <= maxDistance && (seed < 0 || d < seedDistance))
            {
                seed = i;
                seedDistance = d;
            }
        }
        var grove = new List<Target>();
        if (seed < 0)
            return grove;
        var bounds = all.ConvertAll(Bounds);
        var taken = new bool[all.Count];
        var open = new Queue<int>();
        taken[seed] = true;
        open.Enqueue(seed);
        while (open.Count > 0)
        {
            int c = open.Dequeue();
            grove.Add(all[c]);
            for (int i = 0; i < all.Count; i++)
                if (!taken[i] && Gap(bounds[c], bounds[i]) <= ClusterGap)
                {
                    taken[i] = true;
                    open.Enqueue(i);
                }
        }
        return grove;
    }

    /// <summary>目标占的格子（树、木垛 1×1，大树桩 / 大木头 2×2）。</summary>
    private static Rectangle Bounds(Target t) => t.Clump == null
        ? new Rectangle(t.Tile.X, t.Tile.Y, 1, 1)
        : new Rectangle(t.Tile.X, t.Tile.Y, t.Clump.width.Value, t.Clump.height.Value);

    /// <summary>两个矩形之间横、纵坐标差的较大者（挨着为 1，重叠为 0）。</summary>
    private static int Gap(Rectangle a, Rectangle b)
    {
        int dx = Math.Max(0, Math.Max(a.X - (b.Right - 1), b.X - (a.Right - 1)));
        int dy = Math.Max(0, Math.Max(a.Y - (b.Bottom - 1), b.Y - (a.Bottom - 1)));
        return Math.Max(dx, dy);
    }

    /// <summary>
    /// 砍伐顺序：从 start 出发每次接最近的（曼哈顿距离），再做 2-opt：路线上任意两段如果交叉
    /// （把中间那段反过来接更短），就反过来，直到没有可改的或做满 50 轮。几百个目标也就几毫秒，只在开始时算一次。
    /// </summary>
    private static List<Target> PlanOrder(Point start, List<Target> targets)
    {
        var left = new List<Target>(targets);
        var route = new List<Target>(targets.Count);
        var at = start;
        while (left.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < left.Count; i++)
                if (Manhattan(at, left[i].Tile) < Manhattan(at, left[best].Tile))
                    best = i;
            route.Add(left[best]);
            at = left[best].Tile;
            left.RemoveAt(best);
        }

        int n = route.Count;
        Point P(int i) => i < 0 ? start : route[i].Tile;
        for (int pass = 0; pass < 50; pass++)
        {
            bool improved = false;
            for (int i = 0; i < n - 1; i++)
                for (int k = i + 1; k < n; k++)
                {
                    // 把 route[i..k] 反过来：前一个点接 k、i 接 k 的后一个点（路线末尾没有后一个点）
                    int before = Manhattan(P(i - 1), P(i)) + (k + 1 < n ? Manhattan(P(k), P(k + 1)) : 0);
                    int after = Manhattan(P(i - 1), P(k)) + (k + 1 < n ? Manhattan(P(i), P(k + 1)) : 0);
                    if (after < before)
                    {
                        route.Reverse(i, k - i + 1);
                        improved = true;
                    }
                }
            if (!improved)
                break;
        }
        return route;
    }

    private static int Manhattan(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private void Enter(Phase next)
    {
        phase = next;
        PhaseTicks = 0;
    }

    // ---------- 每一刻 ----------

    protected override void Step(Farmer player)
    {
        switch (phase)
        {
            case Phase.Decide: Decide(player); break;
            case Phase.Walk: Walk(player); break;
            case Phase.Start: StartSwing(player); break;
            case Phase.Swing: AfterSwing(player); break;
            case Phase.PileWalk: PileWalk(player); break;
            case Phase.PileWait: PileWait(); break;
            case Phase.Gather:
                if (collector.Tick(player))
                    NextTarget();
                break;
            case Phase.FinalSweep:
                if (collector.Tick(player))
                    Finish();
                break;
        }
    }

    /// <summary>
    /// 目标没了。树：从地图上移除，或树桩已被砍掉（health ≤ −99）——倒树途中砍掉树桩时，
    /// 游戏会让这棵树留到倒完、照常生成那堆木头后再移除。
    /// </summary>
    private bool Gone(Target t) =>
        t.Tree != null
            ? !Location.terrainFeatures.TryGetValue(new Vector2(t.Tile.X, t.Tile.Y), out var tf) || !ReferenceEquals(tf, t.Tree)
              || t.Tree.stump.Value && t.Tree.health.Value <= -99f
            : t.Twig != null
                ? !Location.objects.TryGetValue(new Vector2(t.Tile.X, t.Tile.Y), out var obj) || !ReferenceEquals(obj, t.Twig)
                : t.Clump == null || !Location.resourceClumps.Contains(t.Clump);

    // 木垛没有血量：一斧没碎就算"没造成伤害"。
    private float Health => current?.Tree?.health.Value ?? current?.Clump?.health.Value ?? 1f;

    /// <summary>顺序里排在当前之后、还在的第一个目标（决定倒向、捡完往哪边走）。</summary>
    private Target? Following()
    {
        for (int i = nextIndex; i < order.Count; i++)
            if (!Gone(order[i]))
                return order[i];
        return null;
    }

    private void Decide(Farmer player)
    {
        VirtualInput.Clear();
        if (player.UsingTool || !player.CanMove)
        {
            if (PhaseTicks > WaitTimeout * 3)
                Stop("等太久了，已停止自动砍伐");
            return;
        }
        grid.Flood(player.TilePoint, grid.Map);   // 整张地图约 0.2 ms

        if (current == null || Gone(current))
        {
            // 按顺序取下一个还在的（别人砍掉的跳过）。
            current = null;
            while (nextIndex < order.Count && Gone(order[nextIndex]))
                nextIndex++;
            if (nextIndex >= order.Count)
            {
                AllDone();
                return;
            }
            current = order[nextIndex++];
            hitCurrent = false;
            noProgress = 0;
        }

        var stands = Stands(current).ToList();
        // 会倒下的树：往顺序里下一个目标那侧倒，只用能让它往那边倒的站位。
        if (current.Tree is { } tree && !tree.stump.Value && tree.growthStage.Value >= 5)
        {
            bool fallLeft = ChooseFallLeft(current, player);
            var wanted = stands.FindAll(s => FallsLeft(current, s.Stand, s.Offset) == fallLeft);
            if (wanted.Exists(s => grid.Steps(s.Stand) >= 0))
                stands = wanted;
        }

        (Point Stand, int Dir, float Offset)? pick = null;
        int steps = int.MaxValue;
        foreach (var s in stands)
            if (grid.Steps(s.Stand) is var st && st >= 0 && st < steps)
            {
                steps = st;
                pick = s;
            }
        if (pick == null)
        {
            unreachable++;
            current = null;                // 走不到：跳过，下一刻取顺序里的下一个
            return;
        }
        stand = pick.Value.Stand;
        dir = pick.Value.Dir;
        footOffset = pick.Value.Offset;
        Walker.Begin(grid.Straighten(grid.FloodPath(stand), grid.Map), stand, player, footOffset);
        Enter(Phase.Walk);
    }

    /// <summary>紧挨目标、面朝目标的站位。上下方的站位各给两个脚的偏移（决定倒向）。</summary>
    private IEnumerable<(Point Stand, int Dir, float Offset)> Stands(Target t)
    {
        var tiles = TargetTiles(t).ToList();
        foreach (var c in tiles)
            for (int d = 0; d < 4; d++)
            {
                var s = new Point(c.X - TileGrid.Dirs[d].X, c.Y - TileGrid.Dirs[d].Y);
                if (tiles.Contains(s))
                    continue;
                if (d is 0 or 2 && t.Tree != null)
                {
                    yield return (s, d, -FootShift);
                    yield return (s, d, FootShift);
                }
                else
                {
                    yield return (s, d, 0f);
                }
            }
    }

    /// <summary>与 Tree.performTreeFall 相同：脚（StandingPixel）在树干中线右边就往左倒。</summary>
    private static bool FallsLeft(Target t, Point s, float offset) =>
        s.X * 64 + 32 + offset > (t.Tile.X + 0.5f) * 64f;

    /// <summary>
    /// 给两个落点打分：落点周围的水格（掉进去就没了）、出界重罚；没有可达格能把它吸过来重罚；
    /// 再看离顺序里的下一个目标（没有就是离自己）有多远，磁力半径之内的距离不算。
    /// </summary>
    private bool ChooseFallLeft(Target t, Farmer player)
    {
        Point toward = Following()?.Tile ?? player.TilePoint;
        int magnet = Math.Max(1, (player.GetAppliedMagneticRadius() - 8) / 64);
        float Score(int side)
        {
            var land = new Point(t.Tile.X + side * FallDistance, t.Tile.Y);
            float score = 0f;
            int water = 0, outside = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = land.X + dx, y = land.Y + dy;
                    if (!Location.isTileOnMap(x, y))
                        outside++;
                    else if (Location.isWaterTile(x, y))
                        water++;
                }
            score -= 30f * water + 30f * outside;
            bool collectable = false;
            for (int dy = -magnet; dy <= magnet && !collectable; dy++)
                for (int dx = -magnet; dx <= magnet; dx++)
                    if (grid.Steps(new Point(land.X + dx, land.Y + dy)) >= 0)
                    {
                        collectable = true;
                        break;
                    }
            if (!collectable)
                score -= 100f;
            score -= Math.Max(0, Manhattan(land, toward) - magnet);
            return score;
        }
        return Score(-1) >= Score(1);
    }

    private void Walk(Farmer player)
    {
        if (current == null || Gone(current))
        {
            AfterTarget(player);
            return;
        }
        switch (WalkStep(player, (a, b) => grid.PathBetween(a, b, grid.Map), grid.BlockFor))
        {
            case Walker.Result.Arrived:
                Enter(Phase.Start);
                break;
            case Walker.Result.Lost:
            case Walker.Result.Stuck:
                Enter(Phase.Decide);
                break;
        }
    }

    private void StartSwing(Farmer player)
    {
        VirtualInput.Clear();
        if (current == null || Gone(current))
        {
            AfterTarget(player);
            return;
        }
        if (!ToolUse.Ready(player))
        {
            if (PhaseTicks > WaitTimeout)
                Enter(Phase.Decide);
            return;
        }
        if (player.TilePoint != stand)
        {
            Enter(Phase.Decide);
            return;
        }
        var axe = (Axe)player.CurrentTool;
        float cost = axe.isEfficient.Value ? 0f : 2f - 0.1f * player.ForagingLevel;
        if (player.Stamina - cost < MinStamina)
        {
            Stop("体力不足，已停止自动砍伐");
            return;
        }

        healthBefore = Health;
        ToolUse.Begin(player, dir, Vector2.Zero);   // 不用鼠标位置，砍面前那格
        swings++;
        hitCurrent = true;
        Enter(Phase.Swing);
    }

    private void AfterSwing(Farmer player)
    {
        if (player.UsingTool || !player.CanMove)
        {
            if (PhaseTicks > WaitTimeout)
                Enter(Phase.Decide);
            return;
        }
        if (current == null || Gone(current))
        {
            AfterTarget(player);
            return;
        }
        bool falling = current.Tree?.falling.Value == true;
        if (falling || Health < healthBefore)
        {
            noProgress = 0;
        }
        else if (++noProgress >= 3)
        {
            Notify($"{current.What}砍不动，跳过");
            current = null;
            Enter(Phase.Decide);
            return;
        }
        // 树开始倒了：记下那堆木头的抛出点（Tree.tickUpdate：createRadialDebris(x ± 4, y) → (x·64 + 64, y·64 + 64)）。
        if (falling && fallingTree == null)
        {
            fallingTree = current.Tree;
            int side = fallingTree!.shakeLeft.Value ? -1 : 1;
            pileBase = new Vector2((current.Tile.X + side * FallDistance) * 64 + 64, current.Tile.Y * 64 + 64);
        }
        Enter(Phase.Start);                 // 不等倒树动画，接着砍树桩
    }

    // ---------- 一个目标砍完：就地捡干净 ----------

    /// <summary>当前目标没了：自己砍掉的就把它的掉落物捡干净再走；别人砍掉的直接去下一个。</summary>
    private void AfterTarget(Farmer player)
    {
        VirtualInput.Clear();
        if (current == null)
        {
            Enter(Phase.Decide);
            return;
        }
        foreach (var t in TargetTiles(current))
            grid.Invalidate(t);
        if (!hitCurrent)
        {
            NextTarget();
            return;
        }
        done++;
        if (!Mod.Config.CollectDrops)
        {
            NextTarget();
            return;
        }
        // 木头还没生成（树还在倒）：先走到那堆旁边等着。
        if (fallingTree != null && fallingTree.falling.Value && pileBase is Vector2 pile)
        {
            grid.Flood(player.TilePoint, grid.Map);
            if (PileStand(player, pile) is Point wait)
            {
                Walker.Begin(grid.Straighten(grid.FloodPath(wait), grid.Map), wait, player);
                Enter(Phase.PileWalk);
                return;
            }
            Enter(Phase.PileWait);
            return;
        }
        StartGather(player);
    }

    /// <summary>
    /// 等木头的位置：能吸到预判的 4 件里最多件的可达格；件数相同取走过去步数少的，再相同取离下一个目标近的。
    /// </summary>
    private Point? PileStand(Farmer player, Vector2 pile)
    {
        float r = player.GetAppliedMagneticRadius() - DropCollector.EdgeMargin;
        var center = new Point((int)(pile.X / 64f), (int)(pile.Y / 64f));
        var next = Following()?.Tile ?? player.TilePoint;
        Point? best = null;
        int bestCount = 0, bestSteps = int.MaxValue, bestNext = int.MaxValue;
        int span = (int)(r / 64f) + 2;
        for (int y = center.Y - span; y <= center.Y + span; y++)
            for (int x = center.X - span; x <= center.X + span; x++)
            {
                var c = new Point(x, y);
                int st = grid.Steps(c);
                if (st < 0)
                    continue;
                int count = 0;
                foreach (var o in PileOffsets)
                    if (DropCollector.InMagnet(c, pile + o, r))
                        count++;
                int toNext = Manhattan(c, next);
                if (count > bestCount || count == bestCount && count > 0
                    && (st < bestSteps || st == bestSteps && toNext < bestNext))
                {
                    best = c;
                    bestCount = count;
                    bestSteps = st;
                    bestNext = toNext;
                }
            }
        return best;
    }

    private void PileWalk(Farmer player)
    {
        switch (WalkStep(player, (a, b) => grid.PathBetween(a, b, grid.Map), grid.BlockFor))
        {
            case Walker.Result.Arrived:
                Enter(Phase.PileWait);
                break;
            case Walker.Result.Lost:
            case Walker.Result.Stuck:
                Enter(Phase.PileWait);     // 走不过去就原地等，生成后由拾取来处理
                break;
        }
    }

    /// <summary>等那堆木头生成（树倒完），然后开始捡。</summary>
    private void PileWait()
    {
        VirtualInput.Clear();
        if (fallingTree == null || !fallingTree.falling.Value || PhaseTicks > MaxPileWait)
            StartGather(Game1.player);
    }

    /// <summary>捡这棵树的掉落物：树干与木头堆中点周围 GatherRange 格内，按预判的停稳位置选站位，捡光为止。</summary>
    private void StartGather(Farmer player)
    {
        var trunk = current!.Tile;
        var center = pileBase is Vector2 pile
            ? new Point((trunk.X + (int)(pile.X / 64f)) / 2, (trunk.Y + (int)(pile.Y / 64f)) / 2)
            : trunk;
        collector.Start(Location, grid, center, GatherRange, towards: Following()?.Tile);
        Enter(Phase.Gather);
    }

    private void NextTarget()
    {
        current = null;
        fallingTree = null;
        pileBase = null;
        Enter(Phase.Decide);
    }

    private void AllDone()
    {
        // 兜底：正常情况下每棵树都已捡干净，这里只处理漏网的。
        if (Mod.Config.CollectDrops)
        {
            var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
            int range = Math.Max(area.Width, area.Height) / 2 + FallDistance + 4;
            collector.Start(Location, grid, center, range);
            Enter(Phase.FinalSweep);
            return;
        }
        Finish();
    }

    private void Finish()
    {
        string text = $"砍完了 {done} 个，共 {swings} 斧";
        if (unreachable > 0)
            text += $"，{unreachable} 个走不到已跳过";
        if (collector.Skipped > 0)
            text += $"，{collector.Skipped} 件掉落物捡不到已跳过";
        Stop(text);
    }

    private IEnumerable<Point> TargetTiles(Target t)
    {
        if (t.Clump == null)
        {
            yield return t.Tile;
            yield break;
        }
        for (int y = 0; y < t.Clump.height.Value; y++)
            for (int x = 0; x < t.Clump.width.Value; x++)
                yield return new Point(t.Tile.X + x, t.Tile.Y + y);
    }
}
