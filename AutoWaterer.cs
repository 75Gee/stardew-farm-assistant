using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Enchantments;
using StardewValley.Tools;

namespace FarmAssistant;

/// <summary>
/// 自动浇水的执行：每一刻决定"按住哪些键"，或做一次和玩家按工具键完全相同的起手。
///
/// 走路：按住方向键（虚拟键盘），速度、碰撞、跑 / 走都是游戏自己的。
/// 浇水：朝向目标、和游戏处理工具键时一样调用 BeginUsingTool；要蓄力就按住工具键，到档后松开，
/// 由游戏自己结束并挥动水壶。体力、水量、浇到哪些格子全部由游戏结算。
/// 补水：走到最近的水边，面朝水按一下工具键。
/// </summary>
internal sealed class AutoWaterer : AutoTask
{
    private enum Phase { Idle, Decide, Walk, Start, Charge, Swing }

    private readonly CostModel cost = new();
    private Phase phase = Phase.Idle;
    private FieldPlanner planner = null!;
    private FieldAction action;
    private bool refill;
    private List<Point> covered = new();
    private int swingStart, failedRefills, wateredCount;
    private bool hudWarned;

    public AutoWaterer(ModEntry mod) : base(mod) { }

    public override bool Active => phase != Phase.Idle;
    protected override string Name => "自动浇水";
    protected override string Icon => "(T)WateringCan";

    protected override string? HeldProblem(Farmer player) =>
        player.CurrentTool is WateringCan ? null : "换下了水壶，已停止自动浇水";

    protected override void OnStop() => phase = Phase.Idle;

    // ---------- 开始 / 结束 ----------

    public void Start(Point clicked)
    {
        var player = Game1.player;
        Begin(player.currentLocation);
        planner = new FieldPlanner(new GameFieldView(Location), clicked, player.TilePoint);
        wateredCount = failedRefills = 0;
        hudWarned = false;
        Enter(Phase.Decide);
        Notify($"开始自动浇水：这片地有 {planner.Remaining} 格待浇");
    }

    private void Finish()
    {
        int skipped = planner.Skipped;
        string text = $"浇水完成：浇了 {wateredCount} 格";
        if (skipped > 0)
            text += $"，{skipped} 格够不到已跳过";
        Stop(text);
    }

    private void Enter(Phase next)
    {
        phase = next;
        PhaseTicks = 0;
    }

    // ---------- 每一刻（在游戏读取键盘之前） ----------

    protected override void Step(Farmer player)
    {
        var can = (WateringCan)player.CurrentTool;
        switch (phase)
        {
            case Phase.Decide: Decide(player, can); break;
            case Phase.Walk: Walk(player); break;
            case Phase.Start: StartSwing(player); break;
            case Phase.Charge: Charge(player); break;
            case Phase.Swing: AfterSwing(player, can); break;
        }
    }

    private void Decide(Farmer player, WateringCan can)
    {
        VirtualInput.Clear();
        if (player.UsingTool || !player.CanMove)
        {
            if (PhaseTicks > WaitTimeout)
                Stop("角色一直无法行动，已停止自动浇水");
            return;
        }

        planner.Refresh();
        if (planner.Remaining == 0)
        {
            Finish();
            return;
        }

        bool needWater = !can.IsBottomless && !player.hasWateringCanEnchantment && can.WaterLeft <= 0;
        if (needWater)
        {
            if (failedRefills >= 2)
            {
                Stop("补水失败，已停止自动浇水");
                return;
            }
            var spot = planner.FindRefill(player.TilePoint);
            if (spot == null)
            {
                Stop("附近找不到能补水的地方，已停止自动浇水");
                return;
            }
            refill = true;
            action = new FieldAction(spot.Value.Stand, spot.Value.Dir, 1, null, 0, 0f);
            covered = new List<Point>();
            BeginWalk(spot.Value.Path);
            return;
        }

        UpdateCost(player, can);
        var next = planner.Choose(player.TilePoint, cost, out var route);
        if (next == null)
        {
            Finish();
            return;
        }
        if (player.Stamina - cost.StaminaPerUse(next.Value.Power) < MinStamina)
        {
            Stop("体力不足，已停止自动浇水");
            return;
        }
        refill = false;
        action = next.Value;
        covered = planner.Covered(action);
        BeginWalk(route);
    }

    /// <summary>按当前角色和水壶更新代价模型。</summary>
    private void UpdateCost(Farmer player, WateringCan can)
    {
        bool canCharge = VirtualInput.KeyOf(Game1.options.useToolButton) != Keys.None;
        int reach = can.hasEnchantmentOfType<ReachingToolEnchantment>() ? 1 : 0;
        cost.TileTicks = 64f / Math.Max(1f, player.getMovementSpeed());
        cost.MaxPower = canCharge && can.UpgradeLevel > 0 ? Math.Min(6, can.UpgradeLevel + reach + 1) : 1;
        cost.ChargeTicksPerLevel = 600f * can.AnimationSpeedModifier / 16.667f;
        cost.Efficient = can.isEfficient.Value;
        cost.FarmingLevel = player.FarmingLevel;
        cost.Bottomless = can.IsBottomless || player.hasWateringCanEnchantment;
        cost.AllowClick = Game1.wasMouseVisibleThisFrame && !Game1.options.gamepadControls;
    }

    // ---------- 走路 ----------

    private void BeginWalk(List<Point> route)
    {
        Walker.Begin(planner.Straighten(route), action.Stand, Game1.player);
        Enter(Phase.Walk);
    }

    private void Walk(Farmer player)
    {
        // 队友（或别的原因）浇掉了这一步一半以上的格子：这一步已不划算，重新规划。
        // 每 6 刻查一次，每次最多几十次字典查找。
        if (!refill && PhaseTicks % 6 == 0 && covered.Count(planner.IsTarget) * 2 < covered.Count)
        {
            Enter(Phase.Decide);
            return;
        }
        switch (WalkStep(player, planner.PathBetween, planner.BlockFor))
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

    // ---------- 浇水 ----------

    private void StartSwing(Farmer player)
    {
        VirtualInput.Clear();
        if (!ToolUse.Ready(player))
        {
            if (PhaseTicks > WaitTimeout)
                Enter(Phase.Decide);
            return;
        }
        if (player.TilePoint != action.Stand)
        {
            Enter(Phase.Decide);
            return;
        }
        // 出手前再看一眼：要浇的格子刚被别人浇完了就不白浇。
        if (!refill && covered.TrueForAll(t => !planner.IsTarget(t)))
        {
            Enter(Phase.Decide);
            return;
        }
        // 鼠标停在工具栏等 HUD 上时，按下工具键会变成点击 HUD（游戏本身如此）：等鼠标移开再蓄力。
        if (action.Power > 1 && MouseOverHud())
        {
            if (!hudWarned)
            {
                hudWarned = true;
                Notify("鼠标在工具栏上，移开后继续浇水");
            }
            return;
        }

        if (action.Click is Point c)
        {
            // 用鼠标点脚下或斜角那格：朝向由游戏按点击位置算（脚下那格不转身）。
            var click = new Vector2(c.X * 64 + 32, c.Y * 64 + 32);
            ToolUse.Begin(player, c != action.Stand ? player.getGeneralDirectionTowards(click) : -1, click);
        }
        else
        {
            ToolUse.Begin(player, action.Dir, Vector2.Zero);
        }
        swingStart = Game1.ticks;
        Enter(Phase.Charge);
    }

    private void Charge(Farmer player)
    {
        // 铜壶以下、补水：游戏起手后自己就结束了，不需要松键。
        if (!player.UsingTool || !player.canReleaseTool)
        {
            VirtualInput.Clear();
            if (player.UsingTool || PhaseTicks > 2)
                Enter(Phase.Swing);
            return;
        }
        int wanted = action.Power - 1;
        if (wanted <= 0)
        {
            // 不蓄力：相当于按下立刻松开。
            player.EndUsingTool();
            Enter(Phase.Swing);
            return;
        }
        if (player.toolPower.Value >= wanted || PhaseTicks > wanted * cost.ChargeTicksPerLevel + 60)
        {
            VirtualInput.Clear();      // 松开工具键：游戏在这一刻结束蓄力并挥动
            Enter(Phase.Swing);
            return;
        }
        VirtualInput.Set(new[] { VirtualInput.KeyOf(Game1.options.useToolButton) });
    }

    private void AfterSwing(Farmer player, WateringCan can)
    {
        VirtualInput.Clear();
        if (player.UsingTool || !player.CanMove)
        {
            if (PhaseTicks > WaitTimeout)
                Enter(Phase.Decide);
            return;
        }

        if (refill)
        {
            failedRefills = can.WaterLeft <= 0 ? failedRefills + 1 : 0;
        }
        else
        {
            if (action.Power == 1)
                cost.SwingTicks = cost.SwingTicks * 0.7f + 0.3f * Math.Clamp(Game1.ticks - swingStart, 10, 90);
            foreach (var t in covered)
            {
                if (!planner.IsTarget(t))
                {
                    wateredCount++;
                    continue;
                }
                planner.Failed(t);
            }
        }
        Enter(Phase.Decide);
    }

    private static bool MouseOverHud()
    {
        if (!Game1.wasMouseVisibleThisFrame || !Game1.displayHUD)
            return false;
        Game1.PushUIMode();
        try
        {
            int x = Game1.getMouseX(), y = Game1.getMouseY();
            foreach (var menu in Game1.onScreenMenus)
                if (menu.isWithinBounds(x, y))
                    return true;
            return false;
        }
        finally
        {
            Game1.PopUIMode();
        }
    }
}
