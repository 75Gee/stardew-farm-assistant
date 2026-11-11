using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace FarmAssistant;

/// <summary>
/// 虚拟键盘：把要"按住"的键并进游戏每帧读到的键盘状态（<see cref="Game1.GetKeyboardState"/>）。
/// 游戏照常处理这些键——走路速度、碰撞、蓄力、体力、水量全部走原有逻辑，模组不改任何数据。
/// 玩家自己的按键原样保留。
/// </summary>
internal static class VirtualInput
{
    /// <summary>这一帧要按住的键（由当前分屏的浇水流程每帧设置）。</summary>
    private static readonly HashSet<Keys> Held = new();
    private static int heldScreen = -1;

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Game1), nameof(Game1.GetKeyboardState)),
            postfix: new HarmonyMethod(typeof(VirtualInput), nameof(AfterGetKeyboardState)));
    }

    public static void Set(IEnumerable<Keys> keys)
    {
        Held.Clear();
        foreach (var k in keys)
            if (k != Keys.None)
                Held.Add(k);
        heldScreen = Held.Count > 0 ? Context.ScreenId : -1;
    }

    public static void Clear()
    {
        Held.Clear();
        heldScreen = -1;
    }

    private static void AfterGetKeyboardState(ref KeyboardState __result)
    {
        if (Held.Count == 0 || Context.ScreenId != heldScreen)
            return;
        // 打开菜单、聊天时不注入，交还给玩家。
        if (Game1.activeClickableMenu != null || Game1.IsChatting)
            return;
        var keys = __result.GetPressedKeys();
        var merged = new Keys[keys.Length + Held.Count];
        keys.CopyTo(merged, 0);
        Held.CopyTo(merged, keys.Length);
        __result = new KeyboardState(merged, __result.CapsLock, __result.NumLock);
    }

    /// <summary>上下左右是否都绑了键盘键（自动走路靠它们）。</summary>
    public static bool MovementBound() =>
        KeyOf(Game1.options.moveUpButton) != Keys.None && KeyOf(Game1.options.moveDownButton) != Keys.None
        && KeyOf(Game1.options.moveLeftButton) != Keys.None && KeyOf(Game1.options.moveRightButton) != Keys.None;

    /// <summary>某个游戏操作绑定的第一个键盘键（没绑键盘键时为 None）。</summary>
    public static Keys KeyOf(InputButton[] buttons)
    {
        foreach (var b in buttons)
            if (b.key != Keys.None)
                return b.key;
        return Keys.None;
    }
}

/// <summary>和游戏处理"按下工具键"（Game1.pressUseToolButton）相同的起手。</summary>
internal static class ToolUse
{
    /// <summary>角色站定、空手可以出手了。</summary>
    public static bool Ready(Farmer player) =>
        !player.UsingTool && player.CanMove && player.movementDirections.Count == 0;

    /// <summary>
    /// 清零蓄力、记下点击位置（Vector2.Zero = 不用鼠标、对面前那格）、转向、开始使用工具。
    /// facing &lt; 0 时不改朝向。
    /// </summary>
    public static void Begin(Farmer player, int facing, Vector2 lastClick)
    {
        player.toolPower.Value = 0;
        player.toolHold.Value = 0;
        player.lastClick = lastClick;
        if (facing >= 0)
            player.FacingDirection = facing;
        player.BeginUsingTool();
    }
}
