using Microsoft.Xna.Framework;

namespace FarmAssistant;

/// <summary>
/// 整张地图的格子寻路：通行判定（与农夫走路时同一个碰撞判定）按格缓存，
/// 人或动物挡路时可临时封格。四向 BFS（可限定矩形、可提前结束）用来比较远近；实际行走的路线用 8 方向 A*。
/// 浇水、播种、砍伐、拾取共用这一份实现。
/// </summary>
internal sealed class TileGrid
{
    public static readonly Point[] Dirs = { new(0, -1), new(1, 0), new(0, 1), new(-1, 0) };   // 上 右 下 左（与游戏 FacingDirection 一致）

    private readonly IFieldView view;
    private readonly sbyte[] pass;          // 0 未知，1 可走，-1 不可走
    private readonly Dictionary<int, int> tempBlocked = new();   // 格子 → 到期 tick
    private readonly int[] queue;
    private readonly int[] floodDist, floodParent;
    private Rectangle floodBounds;
    private readonly float[] cost;          // A*：起点到该格的代价
    private readonly int[] seen;            // A*：本次搜索的编号（免得每次清数组）
    private readonly int[] from;
    private int search;

    public int Width { get; }
    public int Height { get; }
    public Rectangle Map => new(0, 0, Width, Height);

    public TileGrid(IFieldView field)
    {
        view = field;
        Width = field.Width;
        Height = field.Height;
        int n = Width * Height;
        pass = new sbyte[n];
        queue = new int[n];
        floodDist = new int[n];
        floodParent = new int[n];
        cost = new float[n];
        seen = new int[n];
        from = new int[n];
    }

    public bool InMap(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool Free(int x, int y)
    {
        if (!InMap(x, y))
            return false;
        int i = y * Width + x;
        if (tempBlocked.Count > 0 && tempBlocked.TryGetValue(i, out int until))
        {
            if (view.Ticks < until)
                return false;
            tempBlocked.Remove(i);
        }
        if (pass[i] == 0)
            pass[i] = view.IsPassable(x, y) ? (sbyte)1 : (sbyte)-1;
        return pass[i] > 0;
    }

    /// <summary>这格的通行情况变了（种下藤架作物、树倒了等）：下次重新判定。</summary>
    public void Invalidate(Point t)
    {
        if (InMap(t.X, t.Y))
            pass[t.Y * Width + t.X] = 0;
    }

    /// <summary>
    /// 被挡住：这格暂时当作不可走，同时丢掉它的通行缓存——到期后重新判定，
    /// 挡路的如果是新放下的箱子、栅栏等永久障碍，就不会反复撞上去。
    /// </summary>
    public void BlockFor(Point t, int ticks)
    {
        if (!InMap(t.X, t.Y))
            return;
        int i = t.Y * Width + t.X;
        tempBlocked[i] = view.Ticks + ticks;
        pass[i] = 0;
    }

    /// <summary>假设 t 不可走时计算一次（用完恢复）。</summary>
    public T WithBlocked<T>(Point t, Func<T> compute)
    {
        int i = t.Y * Width + t.X;
        sbyte old = pass[i];
        pass[i] = -1;
        try
        {
            return compute();
        }
        finally
        {
            pass[i] = old;
        }
    }

    /// <summary>
    /// 从 start 出发的四向 BFS，限定在 bounds 内，步数和来路写进 d / par（-1 = 到不了）。
    /// 给了 stopAt 时，弹出的格子满足它就提前结束并返回该格下标；否则返回 -1。
    /// </summary>
    public int Bfs(Point start, Rectangle bounds, int[] d, int[] par, Func<int, int, bool>? stopAt = null)
    {
        int w = Width;
        bounds = Rectangle.Intersect(bounds, Map);
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            Array.Fill(d, -1, y * w + bounds.Left, bounds.Width);
        if (!bounds.Contains(start))
            return -1;
        int s = start.Y * w + start.X;
        int head = 0, tail = 0;
        d[s] = 0;
        par[s] = -1;
        queue[tail++] = s;
        while (head < tail)
        {
            int c = queue[head++];
            int cx = c % w, cy = c / w;
            if (stopAt != null && stopAt(cx, cy))
                return c;
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + Dirs[k].X, ny = cy + Dirs[k].Y;
                if (nx < bounds.Left || ny < bounds.Top || nx >= bounds.Right || ny >= bounds.Bottom)
                    continue;
                int ni = ny * w + nx;
                if (d[ni] >= 0 || !Free(nx, ny))
                    continue;
                d[ni] = d[c] + 1;
                par[ni] = c;
                queue[tail++] = ni;
            }
        }
        return -1;
    }

    public List<Point> PathTo(Point target, int[] par) => PathTo(target.Y * Width + target.X, par);

    public List<Point> PathTo(int index, int[] par)
    {
        var path = new List<Point>();
        for (int i = index; i >= 0; i = par[i])
            path.Add(new Point(i % Width, i / Width));
        path.Reverse();
        return path;
    }

    // ---------- 给砍伐、拾取用的简单接口（共用一组缓冲，下一次 Flood 前有效） ----------

    /// <summary>从 start 出发把 bounds 内能走到的格子都算出来。</summary>
    public void Flood(Point start, Rectangle bounds)
    {
        floodBounds = Rectangle.Intersect(bounds, Map);
        Bfs(start, floodBounds, floodDist, floodParent);
    }

    /// <summary>上一次 Flood 到 p 的步数，到不了为 -1。</summary>
    public int Steps(Point p) => floodBounds.Contains(p) ? floodDist[p.Y * Width + p.X] : -1;

    /// <summary>上一次 Flood 到 p 的路线。</summary>
    public List<Point> FloodPath(Point p) => PathTo(p, floodParent);

    /// <summary>上一次 Flood 走到的所有格子及步数。</summary>
    public IEnumerable<(Point Tile, int Steps)> Reached()
    {
        for (int y = floodBounds.Top; y < floodBounds.Bottom; y++)
            for (int x = floodBounds.Left; x < floodBounds.Right; x++)
            {
                int d = floodDist[y * Width + x];
                if (d >= 0)
                    yield return (new Point(x, y), d);
            }
    }

    /// <summary>
    /// 实际行走用的路线：8 个方向的 A*（直走一格代价 1、斜走 √2）。游戏里同时按两个方向键时每个方向乘 0.707，
    /// 合速度与直走相同，所以斜走能省下"先横后竖"的折线距离（斜 45° 时省约 29%）。
    /// 斜穿时两侧相邻格都得能走，免得碰撞箱卡在拐角。限定在 bounds 加上两点的外接框内；走不到为 null。
    /// 只探索起点到终点附近的格子，一般不到 0.1 ms。
    /// </summary>
    public List<Point>? PathBetween(Point start, Point target, Rectangle bounds)
    {
        bounds = Rectangle.Intersect(Rectangle.Union(bounds, new Rectangle(Math.Min(start.X, target.X), Math.Min(start.Y, target.Y),
            Math.Abs(start.X - target.X) + 1, Math.Abs(start.Y - target.Y) + 1)), Map);
        if (!bounds.Contains(start) || !bounds.Contains(target))
            return null;
        if (start == target)
            return new List<Point> { start };
        if (++search == int.MaxValue)
        {
            Array.Clear(seen);
            search = 1;
        }
        int w = Width;
        int s = start.Y * w + start.X, goal = target.Y * w + target.X;
        var open = new PriorityQueue<int, float>();
        cost[s] = 0f;
        seen[s] = search;
        from[s] = -1;
        open.Enqueue(s, Heuristic(start, target));
        while (open.TryDequeue(out int c, out float f))
        {
            int cx = c % w, cy = c / w;
            if (c == goal)
                return PathTo(goal, from);
            if (f - Heuristic(new Point(cx, cy), target) > cost[c] + 0.01f)
                continue;                  // 旧的队列项
            for (int k = 0; k < 8; k++)
            {
                int dx = Dirs8[k].X, dy = Dirs8[k].Y;
                int nx = cx + dx, ny = cy + dy;
                if (!bounds.Contains(nx, ny) || !Free(nx, ny))
                    continue;
                bool diagonal = dx != 0 && dy != 0;
                if (diagonal && (!Free(cx + dx, cy) || !Free(cx, cy + dy)))
                    continue;              // 斜穿拐角：两侧都要能走
                float g = cost[c] + (diagonal ? 1.41421356f : 1f);
                int ni = ny * w + nx;
                if (seen[ni] == search && g >= cost[ni])
                    continue;
                seen[ni] = search;
                cost[ni] = g;
                from[ni] = c;
                open.Enqueue(ni, g + Heuristic(new Point(nx, ny), target));
            }
        }
        return null;
    }

    /// <summary>把一条四向路线换成同起终点的 8 方向最短路线（找不到就原样返回）。</summary>
    public List<Point> Straighten(List<Point> route, Rectangle bounds) =>
        route.Count < 3 ? route : PathBetween(route[0], route[^1], bounds) ?? route;

    private static readonly Point[] Dirs8 =
        { new(0, -1), new(1, 0), new(0, 1), new(-1, 0), new(1, -1), new(1, 1), new(-1, 1), new(-1, -1) };

    /// <summary>
    /// 8 方向距离：长边 + (√2 − 1) × 短边。乘 1.001：空旷地上大量格子代价相同，稍微偏向离终点近的，
    /// A* 就不会把整片空地都探一遍（路线长度最多差千分之一）。
    /// </summary>
    private static float Heuristic(Point a, Point b)
    {
        int dx = Math.Abs(a.X - b.X), dy = Math.Abs(a.Y - b.Y);
        return (Math.Max(dx, dy) + 0.41421356f * Math.Min(dx, dy)) * 1.001f;
    }

    /// <summary>以 p 为中心、向外扩 m 格的矩形（裁到地图内）。</summary>
    public Rectangle Around(Point p, int m) =>
        Rectangle.Intersect(new Rectangle(p.X - m, p.Y - m, 2 * m + 1, 2 * m + 1), Map);
}
