using System.Reflection;
using System.Reflection.Emit;
using FarmAssistant.Fishing.Core;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley.Menus;

namespace FarmAssistant.Fishing;

/// <summary>
/// The only change to the game: in <c>BobberBar.update</c> the statement
/// <code>buttonPressed = &lt;mouse / use-tool key / pad X/A&gt;;</code>
/// becomes
/// <code>buttonPressed = BobberBarPatch.Resolve(&lt;same expression&gt;, this);</code>
/// so the assistant supplies exactly what a player's button would: one bool per tick.
/// No other field is ever written.
/// </summary>
internal static class BobberBarPatch
{
    private static bool failed;

    public static void Apply(Harmony harmony, IMonitor monitor)
    {
        MethodInfo target = AccessTools.Method(typeof(BobberBar), nameof(BobberBar.update));
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(BobberBarPatch), nameof(Transpiler)));
        monitor.Log("已挂接 BobberBar.update 的按键读取。", LogLevel.Trace);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        FieldInfo field = AccessTools.Field(typeof(BobberBar), nameof(BobberBar.buttonPressed));
        MethodInfo resolve = AccessTools.Method(typeof(BobberBarPatch), nameof(Resolve));
        int patched = 0;
        foreach (CodeInstruction ins in instructions)
        {
            if (ins.opcode == OpCodes.Stfld && ins.operand is FieldInfo f && f == field)
            {
                // Stack here: [this, pressed].  Branches of the || chain jump to this
                // stfld, so its labels move to the first inserted instruction.
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(ins.labels);
                ins.labels.Clear();
                load.blocks.AddRange(ins.blocks);
                ins.blocks.Clear();
                yield return load;                                  // [this, pressed, this]
                yield return new CodeInstruction(OpCodes.Call, resolve); // [this, decided]
                patched++;
            }
            yield return ins;
        }
        if (patched != 1)
        {
            FishingModule.Instance?.Monitor.Log(
                $"BobberBar.update 中找到 {patched} 处 buttonPressed 赋值（应为 1），游戏版本可能不兼容。",
                patched == 0 ? LogLevel.Error : LogLevel.Warn);
        }
    }

    /// <summary>Called by the game once per active minigame tick, right where it reads the button.</summary>
    public static bool Resolve(bool playerPressed, BobberBar bar)
    {
        FishingModule? mod = FishingModule.Instance;
        if (mod == null || !mod.Enabled || failed)
            return playerPressed;
        try
        {
            // Per split-screen player: each screen has its own minigame, controller and frame count.
            var screen = mod.Screen;
            if (!ReferenceEquals(bar, screen.Bar))
            {
                screen.Bar = bar;
                screen.Tackle = Tackle.FromIds(bar.bobbers);
                mod.Controller.Reset();
                mod.Rounds.Begin(bar);
            }
            else if (screen.Frame != screen.LastFrame + 1)
            {
                mod.Controller.Reset();              // control resumed after a gap
            }
            screen.LastFrame = screen.Frame;
            var tackle = screen.Tackle;

            var obs = new Observation(
                FishPos: bar.bobberPosition,
                FishSpeed: bar.bobberSpeed,
                FloaterSinkerAccel: bar.floaterSinkerAcceleration,
                MotionType: bar.motionType,
                Difficulty: bar.difficulty,
                BarPos: bar.bobberBarPos,
                BarSpeed: bar.bobberBarSpeed,
                BarHeight: bar.bobberBarHeight,
                Tackle: tackle,
                FishInBar: bar.bobberInBar,
                Perfect: bar.perfect,
                Progress: bar.distanceFromCatching,
                TreasureVisible: bar.treasure && !bar.treasureCaught && bar.treasureAppearTimer <= 0f,
                TreasurePos: bar.treasurePosition,
                TreasureLevel: bar.treasureCatchLevel);
            bool held = mod.Controller.Decide(obs);
            mod.Rounds.Tick(obs);
            return held;
        }
        catch (Exception ex)
        {
            failed = true;
            mod.Monitor.Log($"钓鱼辅助出错，本次游戏内已停用，按键交还玩家：\n{ex}", LogLevel.Error);
            return playerPressed;
        }
    }
}
