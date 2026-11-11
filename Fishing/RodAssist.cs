using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;

namespace FarmAssistant.Fishing;

/// <summary>
/// 自动上钩：鱼咬钩时替玩家点一下，走 <c>FishingRod.DoFunction</c>
/// （与游戏自带"自动上钩"附魔同一调用）。
/// </summary>
internal sealed class RodAssist
{
    private readonly FishingModule mod;

    public RodAssist(FishingModule mod) => this.mod = mod;

    /// <summary>Called after every game update.</summary>
    public void Update()
    {
        if (!mod.Enabled || !mod.Config.AutoHook || !Context.IsWorldReady)
            return;
        Farmer player = Game1.player;
        if (player?.CurrentTool is not FishingRod rod || player.currentLocation == null)
            return;
        if (rod.isFishing && rod.isNibbling && !rod.hit && !rod.isReeling && !rod.pullingOutOfWater
            && Game1.activeClickableMenu == null)
        {
            rod.DoFunction(player.currentLocation, (int)rod.bobber.X, (int)rod.bobber.Y, 1, player);
        }
    }
}
