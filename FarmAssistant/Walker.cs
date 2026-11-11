using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace FarmAssistant;

/// <summary>
/// 沿格子路线走：每刻决定按住哪些方向键（虚拟键盘），速度和碰撞都由游戏处理。
/// 路线可以含斜走的一步（同时按两个方向键）。终点会站到格子中央；直走时顺便把另一轴对正，不卡角。
/// </summary>
internal sealed class Walker
{
    public enum Result { Walking, Arrived, Lost, Stuck }

    private const int StuckTicks = 30;

    private List<Point> path = new();
    private int index;
    private Point goal;
    private Vector2 lastPosition;
    private int stillTicks;
    private float offsetX;
    private bool stopAtEnd = true;

    /// <summary>卡住时挡路的那一格。</summary>
    public Point BlockedTile { get; private set; }

    /// <param name="footOffsetX">终点处脚的位置相对格子中心的横向偏移（像素）。砍树时用来选择倒向。</param>
    /// <param name="centerAtEnd">终点是否要站到格子中央；路过的中途格不需要，踩到就算到了，不停顿。</param>
    public void Begin(List<Point> route, Point target, Farmer player, float footOffsetX = 0f, bool centerAtEnd = true)
    {
        offsetX = footOffsetX;
        stopAtEnd = centerAtEnd;
        path = route;
        index = 0;
        goal = target;
        stillTicks = 0;
        lastPosition = player.Position;
    }

    /// <param name="repath">被挤离路线时，从当前格到目标格重新找路。</param>
    public Result Step(Farmer player, Func<Point, Point, List<Point>?> repath)
    {
        var tile = player.TilePoint;
        int at = path.IndexOf(tile, index);
        // 斜走一格时会短暂踩到旁边的拐角格：仍算在这一步上，不重新找路。
        if (at < 0 && index + 1 < path.Count && IsCornerOf(path[index], path[index + 1], tile))
            at = index;
        if (at < 0)
        {
            // 被挤离了路线：从当前位置重新找路。
            var again = repath(tile, goal);
            if (again == null)
            {
                VirtualInput.Clear();
                return Result.Lost;
            }
            path = again;
            at = 0;
        }
        index = at;

        var keys = new List<Keys>(2);
        var here = path[index];
        var center = new Vector2(here.X * 64 + 32, here.Y * 64 + 32);
        var feet = player.StandingPixel;
        float dx = center.X - feet.X, dy = center.Y - feet.Y;
        // 每刻移动约 5 像素（喝咖啡更快），容差要大于半步才不会来回抖；面前那格在 ±24 像素内都准确。
        float tol = Math.Max(4f, player.getMovementSpeed() * 0.6f);

        if (index >= path.Count - 1 && !stopAtEnd)
            return Result.Arrived;          // 中途格：踩到就走下一段，方向键不松
        if (index >= path.Count - 1)
        {
            dx += offsetX;
            if (Math.Abs(dx) > tol)
                keys.Add(dx > 0 ? Right : Left);
            if (Math.Abs(dy) > tol)
                keys.Add(dy > 0 ? Down : Up);
            if (keys.Count == 0)
            {
                VirtualInput.Clear();
                return Result.Arrived;
            }
        }
        else
        {
            var next = path[index + 1];
            int mx = next.X - here.X, my = next.Y - here.Y;
            if (mx != 0 && my != 0)
            {
                // 斜走：两个方向键一起按（游戏里合速度与直走相同）。
                keys.Add(mx > 0 ? Right : Left);
                keys.Add(my > 0 ? Down : Up);
            }
            else if (mx != 0)
            {
                keys.Add(mx > 0 ? Right : Left);
                if (Math.Abs(dy) > tol)
                    keys.Add(dy > 0 ? Down : Up);
            }
            else
            {
                keys.Add(my > 0 ? Down : Up);
                if (Math.Abs(dx) > tol)
                    keys.Add(dx > 0 ? Right : Left);
            }
        }
        VirtualInput.Set(keys);

        // 卡住检测：按着方向键却没动（被人、动物挡住）。
        if (Vector2.DistanceSquared(player.Position, lastPosition) < 0.25f && player.CanMove)
        {
            if (++stillTicks >= StuckTicks)
            {
                stillTicks = 0;
                BlockedTile = index + 1 < path.Count ? path[index + 1] : tile;
                VirtualInput.Clear();
                return Result.Stuck;
            }
        }
        else
        {
            stillTicks = 0;
            lastPosition = player.Position;
        }
        return Result.Walking;
    }

    /// <summary>tile 是从 a 斜走到 b 时经过的两个拐角格之一。</summary>
    private static bool IsCornerOf(Point a, Point b, Point tile) =>
        a.X != b.X && a.Y != b.Y && (tile == new Point(a.X, b.Y) || tile == new Point(b.X, a.Y));

    private static Keys Up => VirtualInput.KeyOf(Game1.options.moveUpButton);
    private static Keys Down => VirtualInput.KeyOf(Game1.options.moveDownButton);
    private static Keys Left => VirtualInput.KeyOf(Game1.options.moveLeftButton);
    private static Keys Right => VirtualInput.KeyOf(Game1.options.moveRightButton);
}
