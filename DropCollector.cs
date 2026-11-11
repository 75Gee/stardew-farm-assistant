using Microsoft.Xna.Framework;
using StardewValley;

namespace FarmAssistant;

/// <summary>
/// 捡掉落物：游戏里掉落物只要进了角色的吸附范围（默认 2 格，戒指可加大）就会自己飞过来，
/// 所以只需要走到"能把它吸过来"的格子上等一下。
///
/// 每一轮：找出范围内要捡的掉落物，按预判的停稳位置（DropPredictor）在可达的格子里选
/// "一站能吸到的件数 ÷ 走过去的步数"最高的那格（相同时取离 towards 近的），走过去，等它们飞进背包。
/// 任何可达格都吸不到的（落进水里、掉到悬崖上等）排除，
/// 排除名单在整个任务里保留（结束时统计跳过数，也不会反复去试）。不移动、不创建任何掉落物，只是走过去。
/// </summary>
internal sealed class DropCollector
{
    private enum Phase { Decide, Walk, Wait, Done }

    private int radius = 12;                // 只管这个范围内的掉落物
    public const float EdgeMargin = 12f;    // 站位离吸附边界留一点余量（像素），抵消碎片停稳前的漂移
    private const int WaitTicks = 90;       // 到位后最多等 1.5 秒让东西飞过来
    private const int MaxStucks = 4;

    private readonly Walker walker = new();
    private readonly HashSet<Debris> excluded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Debris, int> attempts = new(ReferenceEqualityComparer.Instance);
    private GameLocation location = null!;
    private TileGrid grid = null!;
    private Rectangle bounds;
    private Point center;
    private Phase phase = Phase.Done;
    private int phaseTicks, stucks;
    private List<Debris> aiming = new();
    private Point? towards;                 // 捡完要去的方向（下一棵树）：站位相同好时往这边靠

    /// <summary>被排除、至今还躺在地上的件数。</summary>
    public int Skipped => excluded.Count(d => !Gone(d));
    public bool Done => phase == Phase.Done;

    /// <summary>新任务开始：清空排除名单。</summary>
    public void Reset()
    {
        excluded.Clear();
        attempts.Clear();
    }

    /// <summary>在 around 周围 range 格内捡光。寻路用调用方的格子缓存。</summary>
    public void Start(GameLocation loc, TileGrid sharedGrid, Point around, int range, Point? towards = null)
    {
        location = loc;
        grid = sharedGrid;
        center = around;
        radius = range;
        this.towards = towards;
        bounds = grid.Around(around, range + 4);
        stucks = 0;
        Enter(Phase.Decide);
    }

    public void Stop()
    {
        phase = Phase.Done;
        VirtualInput.Clear();
    }

    private void Enter(Phase next)
    {
        phase = next;
        phaseTicks = 0;
    }

    /// <summary>每刻调用；返回 true 表示捡完了（或剩下的都捡不到）。</summary>
    public bool Tick(Farmer player)
    {
        if (phase == Phase.Done)
            return true;
        phaseTicks++;
        switch (phase)
        {
            case Phase.Decide: Decide(player); break;
            case Phase.Walk: Walk(player); break;
            case Phase.Wait: Wait(); break;
        }
        return phase == Phase.Done;
    }

    private void Decide(Farmer player)
    {
        VirtualInput.Clear();
        var drops = Collectible(player);
        if (drops.Count == 0)
        {
            phase = Phase.Done;
            return;
        }

        // 按停稳后的位置选站位（还在弹跳的东西会再挪一段，按现在的位置选常常要再走一步）；会沉进水里的直接排除。
        var final = new Dictionary<Debris, Vector2>(ReferenceEqualityComparer.Instance);
        foreach (var d in drops)
        {
            var f = DropPredictor.Predict(d, location);
            if (f.Sinks)
                Exclude(d);
            else
                final[d] = f.Final;
        }
        drops.RemoveAll(d => !final.ContainsKey(d));
        if (drops.Count == 0)
        {
            phase = Phase.Done;
            return;
        }

        float r = player.GetAppliedMagneticRadius() - EdgeMargin;
        if (!bounds.Contains(player.TilePoint))
            bounds = Rectangle.Union(bounds, grid.Around(player.TilePoint, 4));
        grid.Flood(player.TilePoint, bounds);
        Point best = default;
        float bestScore = -1f;
        var coverable = new HashSet<Debris>(ReferenceEqualityComparer.Instance);
        foreach (var (tile, steps) in grid.Reached())
        {
            int n = 0;
            foreach (var d in drops)
            {
                if (InMagnet(tile, final[d], r))
                {
                    n++;
                    coverable.Add(d);
                }
            }
            if (n == 0)
                continue;
            float score = n / (steps + 3f);
            if (towards is Point to)
                score -= 0.001f * (Math.Abs(tile.X - to.X) + Math.Abs(tile.Y - to.Y));   // 只在打平时起作用
            if (score > bestScore)
            {
                bestScore = score;
                best = tile;
            }
        }

        // 行动可及且吸附可及范围外的：排除。
        foreach (var d in drops)
            if (!coverable.Contains(d))
                Exclude(d);
        if (bestScore < 0f)
        {
            phase = Phase.Done;
            return;
        }

        aiming = drops.FindAll(d => InMagnet(best, final[d], r));
        walker.Begin(grid.Straighten(grid.FloodPath(best), bounds), best, player);
        stucks = 0;                 // 被挡次数按每一站计
        Enter(Phase.Walk);
    }

    private void Walk(Farmer player)
    {
        if (aiming.TrueForAll(Gone))
        {
            Enter(Phase.Decide);
            return;
        }
        switch (walker.Step(player, (a, b) => grid.PathBetween(a, b, bounds)))
        {
            case Walker.Result.Arrived:
                Enter(Phase.Wait);
                break;
            case Walker.Result.Lost:
                Enter(Phase.Decide);
                break;
            case Walker.Result.Stuck:
                if (++stucks > MaxStucks)
                {
                    foreach (var d in aiming)
                        Exclude(d);
                }
                grid.BlockFor(walker.BlockedTile, 300);
                Enter(Phase.Decide);
                break;
        }
    }

    private void Wait()
    {
        VirtualInput.Clear();
        if (aiming.TrueForAll(Gone))
        {
            Enter(Phase.Decide);
            return;
        }
        if (phaseTicks < WaitTicks)
            return;
        // 等了还没过来（背包刚满、被别人吸走一半等）：再试一次，还不行就放弃。
        foreach (var d in aiming)
        {
            if (Gone(d))
                continue;
            attempts.TryGetValue(d, out int n);
            attempts[d] = ++n;
            if (n >= 2)
                Exclude(d);
        }
        Enter(Phase.Decide);
    }

    private void Exclude(Debris d) => excluded.Add(d);

    private bool Gone(Debris d) => Gone(location, d);

    public static bool Gone(GameLocation loc, Debris d) => d.Chunks.Count == 0 || !loc.debris.Contains(d);

    private List<Debris> Collectible(Farmer player)
    {
        var list = new List<Debris>();
        foreach (var d in location.debris)
        {
            if (excluded.Contains(d) || !IsCollectible(d, player))
                continue;
            var p = Position(d);
            if (Math.Abs(p.X / 64f - center.X) > radius || Math.Abs(p.Y / 64f - center.Y) > radius)
                continue;
            list.Add(d);
        }
        return list;
    }

    /// <summary>属于"物品"、没在飞向别的玩家、背包装得下的掉落物。</summary>
    public static bool IsCollectible(Debris d, Farmer player)
    {
        if (d.Chunks.Count == 0)
            return false;
        var type = d.debrisType.Value;
        bool isItem = type is Debris.DebrisType.OBJECT or Debris.DebrisType.ARCHAEOLOGY or Debris.DebrisType.RESOURCE
                      || type == Debris.DebrisType.CHUNKS && d.chunkType.Value == 8;
        if (!isItem || d.player.Value != null && !d.player.Value.IsLocalPlayer)
            return false;
        return d.item != null
            ? player.couldInventoryAcceptThisItem(d.item)
            : player.couldInventoryAcceptThisItem(d.itemId.Value, 1, d.itemQuality);
    }

    /// <summary>
    /// 与游戏判断吸附时同样的位置（Debris.approximatePosition）：所有碎片位置的平均 + 32 像素。
    /// 树倒下的一堆木头是一件掉落物带很多碎片、向四周散开，只看第一块会站错位置。
    /// </summary>
    public static Vector2 Position(Debris d)
    {
        var total = Vector2.Zero;
        foreach (var chunk in d.Chunks)
            total += chunk.position.Value;
        return total / d.Chunks.Count + new Vector2(32f, 32f);
    }

    /// <summary>
    /// 站在 tile 中央时能不能把位于 p 的掉落物吸过来（游戏按横、纵两个方向分别比较，
    /// 都不超过磁力半径就吸；r 已扣掉余量）。
    /// </summary>
    public static bool InMagnet(Point tile, Vector2 p, float r) =>
        Math.Abs(p.X - (tile.X * 64 + 32)) <= r && Math.Abs(p.Y - (tile.Y * 64 + 32)) <= r;
}
