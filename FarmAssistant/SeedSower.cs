using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.TerrainFeatures;
using Object = StardewValley.Object;

namespace FarmAssistant;

/// <summary>
/// 自动播种：手持种子按快捷键后，把离自己最近的那一片空耕地种满。
///
/// 走路同浇水（虚拟方向键）。种下一格用的是游戏处理"右键放置物品"的同一个函数
/// <c>Utility.tryToPlaceItem</c>：同样只能放在角色身边 1 格内，同样扣种子、检查季节。
/// 节奏不快于游戏按住右键时的自动重复（每 0.1 秒一次），边走边种。
/// </summary>
internal sealed class SeedSower : AutoTask
{
    private enum Phase { Idle, Decide, Walk, Plant }

    private const int PlantInterval = 6;     // 刻：与按住右键的重复间隔相同

    private readonly CostModel cost = new() { Neighborhood = true };
    private readonly HashSet<Point> mine = new();
    private Phase phase = Phase.Idle;
    private FieldPlanner planner = null!;
    private string seedId = "";
    private bool raised;
    private FieldAction action;
    private List<Point> covered = new();
    private int lastPlantTick, planted, failsInRow;

    public SeedSower(ModEntry mod) : base(mod) { }

    public override bool Active => phase != Phase.Idle;
    protected override string Name => "自动播种";
    protected override string Icon => "(O)" + seedId;

    protected override string? HeldProblem(Farmer player)
    {
        if (player.ActiveObject is Object held && held.ItemId == seedId)
            return null;
        // 背包里已经没有这种种子 = 用完了；还有 = 玩家换了手持物。
        bool usedUp = !player.Items.Any(i => i is Object o && o.ItemId == seedId);
        return usedUp ? $"种子用完了，已种 {planted} 格" : "换下了种子，已停止自动播种";
    }

    protected override void OnStop() => phase = Phase.Idle;

    public void Start(Point clicked)
    {
        var player = Game1.player;
        if (player.ActiveObject is not { Category: Object.SeedsCategory } seed)
            return;
        seedId = seed.ItemId;
        var view = new GameFieldView(player.currentLocation, FieldMode.Seed, seedId);
        if (!view.IsTarget(clicked))
        {
            Notify("这种种子现在不能种在这里");
            return;
        }
        Begin(player.currentLocation);
        raised = view.SeedIsRaised;
        planner = new FieldPlanner(view, clicked, player.TilePoint);
        planted = failsInRow = 0;
        mine.Clear();
        lastPlantTick = -PlantInterval;
        Enter(Phase.Decide);
        Notify($"开始自动播种：这片地有 {planner.Remaining} 格空地");
    }

    private void Finish()
    {
        int skipped = planner.Skipped;
        Stop(skipped > 0 ? $"播种完成：种了 {planted} 格，{skipped} 格种不了已跳过" : $"播种完成：种了 {planted} 格");
    }

    private void Enter(Phase next)
    {
        phase = next;
        PhaseTicks = 0;
    }

    protected override void Step(Farmer player)
    {
        switch (phase)
        {
            case Phase.Decide: Decide(player); break;
            case Phase.Walk: Walk(player); break;
            case Phase.Plant: PlantHere(player); break;
        }
    }

    private void Decide(Farmer player)
    {
        VirtualInput.Clear();
        if (!player.CanMove)
        {
            if (PhaseTicks > WaitTimeout)
                Stop("角色一直无法行动，已停止自动播种");
            return;
        }
        planner.Refresh();
        if (planner.Remaining == 0)
        {
            Finish();
            return;
        }
        cost.TileTicks = 64f / Math.Max(1f, player.getMovementSpeed());
        var next = planner.Choose(player.TilePoint, cost, out var route);
        if (next == null)
        {
            Finish();
            return;
        }
        action = next.Value;
        covered = planner.Covered(action);
        Walker.Begin(planner.Straighten(route), action.Stand, player);
        Enter(Phase.Walk);
    }

    private void Walk(Farmer player)
    {
        // 队友种掉了这一站一半以上的空地：重新规划（自己顺路种下的不算）。
        if (PhaseTicks % 6 == 0 && covered.Count(t => mine.Contains(t) || planner.IsPending(t)) * 2 < covered.Count)
        {
            Enter(Phase.Decide);
            return;
        }
        // 边走边种（藤架作物例外：种下后那格不能走，只在站定时种）。
        if (!raised)
            TryPlantAround(player, preferred: null);

        switch (WalkStep(player, planner.PathBetween, planner.BlockFor))
        {
            case Walker.Result.Arrived:
                Enter(Phase.Plant);
                break;
            case Walker.Result.Lost:
            case Walker.Result.Stuck:
                Enter(Phase.Decide);
                break;
        }
    }

    private void PlantHere(Farmer player)
    {
        VirtualInput.Clear();
        if (player.TilePoint != action.Stand)
        {
            Enter(Phase.Decide);
            return;
        }
        bool any = false;
        foreach (var t in covered)
            if (planner.IsPending(t))
            {
                any = true;
                break;
            }
        if (!any)
        {
            Enter(Phase.Decide);
            return;
        }
        if (!TryPlantAround(player, covered))
        {
            // 剩下的格子种上藤架作物会把路堵死：跳过它们（结束时计入"种不了"）。
            foreach (var t in covered)
                if (planner.IsPending(t))
                    planner.Exclude(t);
            Enter(Phase.Decide);
            return;
        }
        if (PhaseTicks > WaitTimeout)
            Enter(Phase.Decide);
    }

    /// <summary>每 0.1 秒最多种一格：身边 8 格里属于这片地、还空着的那格。没有可安全种下的格子时返回 false。</summary>
    private bool TryPlantAround(Farmer player, List<Point>? preferred)
    {
        if (Game1.ticks - lastPlantTick < PlantInterval)
            return true;
        var here = player.TilePoint;
        Point? pick = null;
        if (preferred != null)
        {
            foreach (var t in preferred)
                if (Math.Abs(t.X - here.X) <= 1 && Math.Abs(t.Y - here.Y) <= 1 && t != here && planner.IsPending(t) && Safe(t, here))
                {
                    pick = t;
                    break;
                }
        }
        else
        {
            foreach (var o in FieldPlanner.Ring)
            {
                var t = new Point(here.X + o.X, here.Y + o.Y);
                if (planner.IsPending(t))
                {
                    pick = t;
                    break;
                }
            }
        }
        if (pick is not Point target)
            return false;

        lastPlantTick = Game1.ticks;
        // 与游戏处理右键放置物品时同一个函数：范围、季节、扣种子、联机同步都由它处理。
        Utility.tryToPlaceItem(Location, player.ActiveObject, target.X * 64 + 32, target.Y * 64 + 32);
        if (Location.terrainFeatures.TryGetValue(new Vector2(target.X, target.Y), out var tf) && tf is HoeDirt { crop: { } crop })
        {
            planted++;
            failsInRow = 0;
            mine.Add(target);
            // 按实际种出来的作物判断（混合种子种下才知道是什么）：藤架作物那格从此不能走。
            if (raised || crop.raisedSeeds.Value)
                planner.Invalidate(target);
            return true;
        }
        planner.Failed(target);
        if (++failsInRow >= 6)
            Stop("连续种不下去，已停止自动播种");
        return true;
    }

    private bool Safe(Point t, Point here) => !raised || planner.SafeToBlock(t, here);
}
