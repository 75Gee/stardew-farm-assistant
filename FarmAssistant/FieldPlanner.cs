using Microsoft.Xna.Framework;

namespace FarmAssistant;

/// <summary>
/// 一次动作：站在 Stand。浇水时朝 Dir 蓄力到 Power 档（1 = 不蓄力），Click 非空时是鼠标点选身边 3×3 内的那一格；
/// 播种时 Power = 0，表示把身边 8 格里能种的都种上。
/// </summary>
internal readonly record struct FieldAction(Point Stand, int Dir, int Power, Point? Click, int Gain, float Cost);

/// <summary>
/// 规划：在一片地上反复选"下一步最划算的动作"（新完成的格数 ÷ 走路 + 动作 + 体力 + 用水的代价），
/// 并对前几名候选再往后看一步，避免贪心留下零散的格子。每次动作前用游戏的实时状态刷新：
/// 队友做过的格子自然跳过；这片地新锄的、新种的格子（增量）也会被并进来。
/// 走不到、任何站位都处理不到的格子留在最后统计为跳过。
/// </summary>
internal sealed class FieldPlanner
{
    private static Point[] Dirs => TileGrid.Dirs;
    /// <summary>鼠标点选的格子：脚下和 4 个斜角（正前后左右用朝向即可）。</summary>
    private static readonly Point[] ClickOffsets = { new(0, 0), new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };
    /// <summary>身边 8 格（播种的范围：游戏允许放在角色周围 1 格内）。</summary>
    public static readonly Point[] Ring = { new(-1, -1), new(0, -1), new(1, -1), new(-1, 0), new(1, 0), new(-1, 1), new(0, 1), new(1, 1) };
    private const int MaxPower = 6;
    private const int ClusterGap = 3;      // 相距 ≤ 3 格的菜地算同一片
    private const int Margin = 8;          // 规划区域在菜地外扩的格数
    private const int LookaheadCount = 6;

    private readonly IFieldView view;
    private readonly TileGrid grid;
    private readonly int w, h;
    private readonly bool[] rem;           // 仍待浇
    private readonly bool[] near;          // 离待浇格 ≤ Reach 格的站位（只在这些格子上评估）
    private readonly bool[] member;        // 属于这片地的耕地（含已完成的），增量蔓延时用
    private readonly List<Point> members = new();
    private int reach = 6;                 // 站位离目标最远多少格还有用（浇水 3×6 为 6，播种为 1）
    private readonly HashSet<Point> excluded = new();
    private readonly Dictionary<Point, int> failures = new();
    private readonly int[] dist, parent, dist2, parent2;
    private readonly Point[][][] shapes;   // [档位][方向] → 相对站位的偏移
    private readonly Rectangle[][] boxes;  // 同上，形状都是矩形：外接框（相对站位）
    private readonly int[] prefix;         // 规划区域内待浇格的二维前缀和
    private Rectangle region;

    public int Remaining { get; private set; }

    public FieldPlanner(IFieldView field, Point clicked, Point player)
    {
        view = field;
        grid = new TileGrid(field);
        w = field.Width;
        h = field.Height;
        int n = w * h;
        rem = new bool[n];
        near = new bool[n];
        member = new bool[n];
        dist = new int[n];
        parent = new int[n];
        dist2 = new int[n];
        parent2 = new int[n];
        shapes = BuildShapes();
        boxes = new Rectangle[MaxPower + 1][];
        for (int p = 1; p <= MaxPower; p++)
        {
            boxes[p] = new Rectangle[4];
            for (int dir = 0; dir < 4; dir++)
            {
                var s = shapes[p][dir];
                int bx0 = s.Min(o => o.X), by0 = s.Min(o => o.Y), bx1 = s.Max(o => o.X), by1 = s.Max(o => o.Y);
                boxes[p][dir] = new Rectangle(bx0, by0, bx1 - bx0 + 1, by1 - by0 + 1);
            }
        }
        prefix = new int[(w + 1) * (h + 1)];
        region = Expand(new Rectangle(player.X, player.Y, 1, 1), Margin);
        if (view.IsTarget(clicked))
            Spread(new List<Point> { clicked });
        Refresh();
    }

    private Rectangle Expand(Rectangle r, int m) =>
        Rectangle.Intersect(new Rectangle(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m), new Rectangle(0, 0, w, h));

    /// <summary>补水回来时人可能在规划区域外：把区域扩到包含当前位置。</summary>
    private void Include(Point p)
    {
        if (!region.Contains(p))
            region = Rectangle.Union(region, Expand(new Rectangle(p.X, p.Y, 1, 1), Margin));
    }

    // ---------- 菜地 ----------

    public bool IsTarget(Point tile) => view.IsTarget(tile);

    /// <summary>属于这片地、还没放弃、仍待处理。</summary>
    public bool IsPending(Point t) =>
        InMap(t.X, t.Y) && member[t.Y * w + t.X] && !excluded.Contains(t) && view.IsTarget(t);

    /// <summary>
    /// 从种子格出发沿耕地蔓延（含已完成的、空的耕地），相距 ≤ ClusterGap 格算相连。
    /// 先手动处理过的一条不会把一片地切成两半。新加入的格子把规划区域撑大。
    /// </summary>
    private void Spread(List<Point> seeds)
    {
        var open = new Queue<Point>();
        foreach (var s in seeds)
            if (InMap(s.X, s.Y) && !member[s.Y * w + s.X])
            {
                member[s.Y * w + s.X] = true;
                members.Add(s);
                open.Enqueue(s);
            }
        if (open.Count == 0)
            return;
        while (open.Count > 0)
        {
            var c = open.Dequeue();
            region = Rectangle.Union(region, Expand(new Rectangle(c.X, c.Y, 1, 1), Margin));
            for (int dy = -ClusterGap; dy <= ClusterGap; dy++)
                for (int dx = -ClusterGap; dx <= ClusterGap; dx++)
                {
                    int x = c.X + dx, y = c.Y + dy;
                    if (!InMap(x, y) || member[y * w + x] || !view.IsSoil(x, y))
                        continue;
                    member[y * w + x] = true;
                    var q = new Point(x, y);
                    members.Add(q);
                    open.Enqueue(q);
                }
        }
    }

    /// <summary>
    /// 增量：规划区域里新出现的耕地（新锄的、花盆），只要离这片地 ≤ ClusterGap 格就并进来。
    /// 每次约区域格数次字典查找（30×30 的地约 2000 次）。
    /// </summary>
    private void Grow()
    {
        List<Point>? found = null;
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
            {
                if (member[y * w + x] || !view.IsSoil(x, y) || !NearMember(x, y))
                    continue;
                (found ??= new List<Point>()).Add(new Point(x, y));
            }
        if (found != null)
            Spread(found);
    }

    private bool NearMember(int x, int y)
    {
        int y0 = Math.Max(0, y - ClusterGap), y1 = Math.Min(h - 1, y + ClusterGap);
        int x0 = Math.Max(0, x - ClusterGap), x1 = Math.Min(w - 1, x + ClusterGap);
        for (int yy = y0; yy <= y1; yy++)
            for (int xx = x0; xx <= x1; xx++)
                if (member[yy * w + xx])
                    return true;
        return false;
    }

    /// <summary>按游戏的实时状态刷新待办格：已完成的消失，这片地新出现的（增量）加入。</summary>
    public void Refresh()
    {
        Grow();
        Remaining = 0;
        for (int y = region.Top; y < region.Bottom; y++)
            Array.Fill(near, false, y * w + region.Left, region.Width);
        foreach (var t in members)
        {
            bool r = !excluded.Contains(t) && IsTarget(t);
            rem[t.Y * w + t.X] = r;
            if (!r)
                continue;
            Remaining++;
            int ny0 = Math.Max(region.Top, t.Y - reach), ny1 = Math.Min(region.Bottom - 1, t.Y + reach);
            int nx0 = Math.Max(region.Left, t.X - reach), nx1 = Math.Min(region.Right - 1, t.X + reach);
            for (int y = ny0; y <= ny1; y++)
                for (int x = nx0; x <= nx1; x++)
                    near[y * w + x] = true;
        }
    }

    /// <summary>不再尝试这格（结束时计入跳过）。</summary>
    public void Exclude(Point t) => excluded.Add(t);

    /// <summary>这格做了却没成功：连续两次就不再尝试。</summary>
    public void Failed(Point t)
    {
        failures.TryGetValue(t, out int n);
        failures[t] = ++n;
        if (n >= 2)
            excluded.Add(t);
    }

    /// <summary>结束时的跳过数：还没做的（够不到的）加上放弃了的。</summary>
    public int Skipped
    {
        get
        {
            Refresh();
            return Remaining + excluded.Count(view.IsTarget);
        }
    }

    // ---------- 通行 ----------

    public bool InMap(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;

    /// <summary>这格的通行情况变了（例如刚种下藤架作物）：下次重新判定。</summary>
    public void Invalidate(Point t) => grid.Invalidate(t);

    /// <summary>被人或动物挡住：这格暂时当作不可走。</summary>
    public void BlockFor(Point t, int ticks) => grid.BlockFor(t, ticks);

    /// <summary>
    /// 在 t 种下藤架作物后它就不能走了：只有不会因此让剩下的格子（或自己）被围死时才种。
    /// 两次 BFS，只在种藤架类种子时用。
    /// </summary>
    public bool SafeToBlock(Point t, Point from)
    {
        int before = Coverable(from, t);
        int after = grid.WithBlocked(t, () => Coverable(from, t));
        return after >= before;
    }

    /// <summary>从 from 出发能站到其旁边（8 邻）的待办格数，不含 skip。</summary>
    private int Coverable(Point from, Point skip)
    {
        Bfs(from, region, dist2, parent2);
        int n = 0;
        foreach (var m in members)
        {
            if (m == skip || !rem[m.Y * w + m.X])
                continue;
            foreach (var o in Ring)
            {
                int x = m.X + o.X, y = m.Y + o.Y;
                if (region.Contains(x, y) && dist2[y * w + x] >= 0)
                {
                    n++;
                    break;
                }
            }
        }
        return n;
    }

    private void Bfs(Point start, Rectangle bounds, int[] d, int[] par) => grid.Bfs(start, bounds, d, par);

    private List<Point> PathTo(Point target, int[] par) => grid.PathTo(target, par);

    // ---------- 水壶形状（与 Tool.tilesAffected 一致） ----------

    /// <summary>
    /// 以"面前那格"为原点，沿朝向前进 f、横向 l：
    /// 1 档 1 格；2 档前 3 格；3 档前 5 格；4 档 3×3；5 档 3 宽 × 6 深；6 档（铱壶 + 远程附魔）5×5。
    /// </summary>
    private static Point[][][] BuildShapes()
    {
        var all = new Point[MaxPower + 1][][];
        for (int p = 1; p <= MaxPower; p++)
        {
            (int f0, int f1, int l) = p switch
            {
                1 => (0, 0, 0),
                2 => (0, 2, 0),
                3 => (0, 4, 0),
                4 => (0, 2, 1),
                5 => (0, 5, 1),
                _ => (0, 4, 2),
            };
            all[p] = new Point[4][];
            for (int dir = 0; dir < 4; dir++)
            {
                var fwd = Dirs[dir];
                var lat = new Point(-fwd.Y, fwd.X);
                var list = new List<Point>();
                for (int f = f0; f <= f1; f++)
                    for (int s = -l; s <= l; s++)
                        list.Add(new Point(fwd.X * (1 + f) + lat.X * s, fwd.Y * (1 + f) + lat.Y * s));
                all[p][dir] = list.ToArray();
            }
        }
        return all;
    }

    public List<Point> Covered(in FieldAction a)
    {
        var list = new List<Point>();
        if (a.Click is Point c)
        {
            if (InMap(c.X, c.Y) && rem[c.Y * w + c.X])
                list.Add(c);
            return list;
        }
        if (a.Power == 0)
        {
            foreach (var o in Ring)
            {
                int x = a.Stand.X + o.X, y = a.Stand.Y + o.Y;
                if (InMap(x, y) && rem[y * w + x])
                    list.Add(new Point(x, y));
            }
            return list;
        }
        foreach (var o in shapes[a.Power][a.Dir])
        {
            int x = a.Stand.X + o.X, y = a.Stand.Y + o.Y;
            if (InMap(x, y) && rem[y * w + x])
                list.Add(new Point(x, y));
        }
        return list;
    }

    // ---------- 选择 ----------

    /// <summary>选下一步动作；没有任何可浇的动作时返回 null（剩下的都够不到）。</summary>
    public FieldAction? Choose(Point start, CostModel cost, out List<Point> path)
    {
        path = new List<Point>();
        int wantReach = cost.Neighborhood ? 1 : cost.MaxPower >= 5 ? 6 : cost.MaxPower >= 3 ? 5 : 3;
        if (wantReach != reach)
        {
            reach = wantReach;
            Refresh();
        }
        if (Remaining == 0)
            return null;
        if (!region.Contains(start))
        {
            Include(start);
            Refresh();
        }
        Bfs(start, region, dist, parent);
        var top = new FieldAction[LookaheadCount];
        int count = Evaluate(dist, cost, top);
        if (count == 0)
            return null;

        int best = 0;
        float bestRate = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            var a = top[i];
            float gain = a.Gain, time = a.Cost;
            // 往后看一步：做完这个动作后，从它的站位出发的最好下一步。
            var covered = Covered(a);
            foreach (var t in covered)
                rem[t.Y * w + t.X] = false;
            if (Remaining - covered.Count > 0)
            {
                Bfs(a.Stand, region, dist2, parent2);
                var next = new FieldAction[1];
                if (Evaluate(dist2, cost, next) > 0)
                {
                    gain += next[0].Gain;
                    time += next[0].Cost;
                }
            }
            foreach (var t in covered)
                rem[t.Y * w + t.X] = true;
            float rate = gain / time;
            if (rate > bestRate)
            {
                bestRate = rate;
                best = i;
            }
        }
        var chosen = top[best];
        path = PathTo(chosen.Stand, parent);
        return chosen;
    }

    /// <summary>在所有可达站位 × 朝向 × 档位里取单步效率（新浇格数 ÷ 代价）最高的若干个。</summary>
    private int Evaluate(int[] d, CostModel cost, FieldAction[] top)
    {
        int count = 0, worst = 0;
        float worstRate = float.MinValue;      // 名单满了之后，低于它的候选直接丢弃
        var rates = new float[top.Length];
        // 先只用数字判断能否进前几名，进了才构造动作。
        void Offer(Point stand, int dir, int power, Point? click, int gain, float actionCost)
        {
            float rate = gain / actionCost;
            if (count == top.Length && rate <= worstRate)
                return;
            var a = new FieldAction(stand, dir, power, click, gain, actionCost);
            if (count < top.Length)
            {
                top[count] = a;
                rates[count++] = rate;
                if (count < top.Length)
                    return;
            }
            else
            {
                top[worst] = a;
                rates[worst] = rate;
            }
            worst = 0;
            for (int i = 1; i < count; i++)
                if (rates[i] < rates[worst])
                    worst = i;
            worstRate = rates[worst];
        }

        // 形状都是矩形：先对规划区域做一次二维前缀和，之后每个候选 O(1) 数格子。
        int rw = region.Width, rh = region.Height, stride = rw + 1;
        Array.Clear(prefix, 0, stride);                // 区域会变大，首行首列必须清零
        for (int y = 0; y < rh; y++)
        {
            prefix[(y + 1) * stride] = 0;
            int row = 0, src = (region.Top + y) * w + region.Left;
            for (int x = 0; x < rw; x++)
            {
                if (rem[src + x])
                    row++;
                prefix[(y + 1) * stride + x + 1] = prefix[y * stride + x + 1] + row;
            }
        }
        int RectSum(int x0, int y0, int x1, int y1)   // 地图坐标，左上含、右下不含
        {
            x0 = Math.Max(x0, region.Left) - region.Left; y0 = Math.Max(y0, region.Top) - region.Top;
            x1 = Math.Min(x1, region.Right) - region.Left; y1 = Math.Min(y1, region.Bottom) - region.Top;
            if (x1 <= x0 || y1 <= y0)
                return 0;
            return prefix[y1 * stride + x1] - prefix[y0 * stride + x1] - prefix[y1 * stride + x0] + prefix[y0 * stride + x0];
        }
        Span<float> actionCost = stackalloc float[MaxPower + 1];
        for (int p = 1; p <= cost.MaxPower; p++)
            actionCost[p] = cost.ActionCost(p);

        for (int y = region.Top; y < region.Bottom; y++)
        {
            for (int x = region.Left; x < region.Right; x++)
            {
                int steps = d[y * w + x];
                if (steps < 0 || !near[y * w + x])
                    continue;
                float walk = steps * cost.TileTicks;
                var stand = new Point(x, y);
                if (cost.Neighborhood)
                {
                    // 播种：身边 3×3 去掉脚下那格。
                    int g = RectSum(x - 1, y - 1, x + 2, y + 2) - (rem[y * w + x] ? 1 : 0);
                    if (g > 0)
                        Offer(stand, -1, 0, null, g, walk + cost.PlantCost(g));
                    continue;
                }
                bool frontPending = false;
                for (int dir = 0; dir < 4; dir++)
                {
                    for (int p = 1; p <= cost.MaxPower; p++)
                    {
                        var b = boxes[p][dir];
                        int gain = RectSum(x + b.X, y + b.Y, x + b.X + b.Width, y + b.Y + b.Height);
                        if (gain == 0)
                            continue;
                        if (p == 1)
                            frontPending = true;
                        Offer(stand, dir, p, null, gain, walk + actionCost[p]);
                    }
                }
                // 不蓄力时可以用鼠标点脚下或斜角那一格（玩家同样能做到）。它和"朝前浇一格"代价相同，
                // 所以只在前后左右都没有待办格时才需要，且每个站位取一个即可。
                if (cost.AllowClick && !frontPending)
                {
                    for (int k = 0; k < ClickOffsets.Length; k++)
                    {
                        int tx = x + ClickOffsets[k].X, ty = y + ClickOffsets[k].Y;
                        if (InMap(tx, ty) && rem[ty * w + tx])
                        {
                            Offer(stand, -1, 1, new Point(tx, ty), 1, walk + actionCost[1]);
                            break;
                        }
                    }
                }
            }
        }
        return count;
    }

    /// <summary>最近的补水点：站位 + 朝向（面前那格可以灌水）。整张地图找，找到最近的就停。</summary>
    public (Point Stand, int Dir, List<Point> Path)? FindRefill(Point start)
    {
        int RefillDir(int x, int y)
        {
            for (int k = 0; k < 4; k++)
            {
                int fx = x + Dirs[k].X, fy = y + Dirs[k].Y;
                if (InMap(fx, fy) && view.CanRefill(fx, fy))
                    return k;
            }
            return -1;
        }
        int found = grid.Bfs(start, grid.Map, dist2, parent2, (x, y) => RefillDir(x, y) >= 0);
        if (found < 0)
            return null;
        var stand = new Point(found % w, found / w);
        return (stand, RefillDir(stand.X, stand.Y), grid.PathTo(found, parent2));
    }

    /// <summary>从 start 到 target 的行走路线（8 方向，走不到时为 null）。</summary>
    public List<Point>? PathBetween(Point start, Point target) => grid.PathBetween(start, target, region);

    /// <summary>把规划出的四向路线换成同起终点的 8 方向路线（可以斜着走）。</summary>
    public List<Point> Straighten(List<Point> route) => grid.Straighten(route, region);
}

/// <summary>代价（单位：游戏 tick，60 = 1 秒）。</summary>
internal sealed class CostModel
{
    public float TileTicks = 12f;          // 走一格
    public int MaxPower = 1;               // 当前水壶能蓄到的最高档
    public float ChargeTicksPerLevel = 36f;
    public float SwingTicks = 26f;         // 挥动水壶（运行中按实测更新）
    public float StaminaPerUse(int p) => Efficient ? 0f : Math.Max(0f, 2f * p - 0.1f * FarmingLevel);
    public bool AllowClick;
    /// <summary>播种模式：每个站位把身边 8 格种上。</summary>
    public bool Neighborhood;
    /// <summary>种一格的间隔（游戏按住右键时每 0.1 秒重复一次）。</summary>
    public float PlantTicks = 6f;
    public float PlantCost(int tiles) => SetupTicks + PlantTicks * tiles;

    public bool Efficient;
    public int FarmingLevel;
    public bool Bottomless;

    private const float SetupTicks = 6f;
    private const float TicksPerStamina = 3f;   // 体力也算进代价：同样的格数，少花体力更好
    private const float TicksPerWater = 2f;     // 用水越多越早要去补水

    public float ActionCost(int p) =>
        SetupTicks + (p - 1) * ChargeTicksPerLevel + SwingTicks
        + TicksPerStamina * StaminaPerUse(p)
        + (Bottomless ? 0f : TicksPerWater * p);
}
