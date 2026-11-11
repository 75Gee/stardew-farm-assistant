using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace FarmAssistant;

/// <summary>
/// 浇水、播种、砍伐三个执行器的共同部分：每刻的通用检查（离开地点、打开菜单、进入事件或对话、
/// 骑马、换下手持物 → 停止；窗口失焦 → 原地等待），停止时松开所有虚拟按键并提示，走路时的卡住处理。
/// </summary>
internal abstract class AutoTask
{
    protected const int MinStamina = 10;     // 体力低于它就停，不把人累倒
    protected const int WaitTimeout = 180;   // 等角色能行动最多 3 秒
    private const int MaxStucks = 6;
    private const int BlockTicks = 300;      // 挡路的格子暂时封 5 秒

    protected readonly ModEntry Mod;
    protected readonly Walker Walker = new();
    protected GameLocation Location = null!;
    protected int PhaseTicks;
    private int stucks;

    protected AutoTask(ModEntry mod) => Mod = mod;

    public abstract bool Active { get; }

    /// <summary>功能名，用在提示里（"已停止自动浇水"）。</summary>
    protected abstract string Name { get; }

    /// <summary>提示框里的图标（物品 ID）。</summary>
    protected abstract string Icon { get; }

    /// <summary>手持物不对时返回停止原因，否则 null。</summary>
    protected abstract string? HeldProblem(Farmer player);

    /// <summary>通过通用检查后的一刻。</summary>
    protected abstract void Step(Farmer player);

    /// <summary>回到空闲状态并清理自己的子状态。</summary>
    protected abstract void OnStop();

    /// <summary>开始一次任务时调用。</summary>
    protected void Begin(GameLocation location)
    {
        Location = location;
        stucks = 0;
    }

    public void Tick()
    {
        if (!Active)
            return;
        var player = Game1.player;
        if (!Context.IsWorldReady || player.currentLocation != Location)
        {
            Stop($"离开了这里，已停止{Name}");
            return;
        }
        if (Game1.activeClickableMenu != null || Game1.eventUp || Game1.dialogueUp || player.isRidingHorse())
        {
            Stop($"已停止{Name}");
            return;
        }
        if (HeldProblem(player) is { } why)
        {
            Stop(why);
            return;
        }
        if (!Game1.game1.IsActive)
            return;   // 窗口失焦时游戏不读键盘，原地等待
        PhaseTicks++;
        Step(player);
    }

    public void Stop(string? reason = null)
    {
        if (!Active)
            return;
        OnStop();
        VirtualInput.Clear();
        if (reason != null)
            Notify(reason);
    }

    /// <summary>玩家自己按了键：停下交还控制。</summary>
    public void TakeOver() => Stop($"已手动接管，停止{Name}");

    protected void Notify(string text) => Mod.Notify(text, Icon);

    /// <summary>
    /// 走一刻路。被挡住时把挡路的格子暂时封掉（之后应重新规划）；去同一站途中被挡次数太多就停止并返回 null。
    /// </summary>
    protected Walker.Result? WalkStep(Farmer player, Func<Point, Point, List<Point>?> repath, Action<Point, int> block)
    {
        var result = Walker.Step(player, repath);
        if (result == Walker.Result.Arrived)
            stucks = 0;                    // 被挡次数按每一段路计：长任务里零星被挡不会累计到停止
        if (result != Walker.Result.Stuck)
            return result;
        if (++stucks > MaxStucks)
        {
            Stop($"路被挡住了，已停止{Name}");
            return null;
        }
        block(Walker.BlockedTile, BlockTicks);
        return result;
    }
}
