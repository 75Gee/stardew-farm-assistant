using FarmAssistant.Fishing.Core;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace FarmAssistant.Fishing;

/// <summary>One line per controlled round in the SMAPI console.</summary>
internal sealed class RoundLog
{
    private readonly IMonitor monitor;
    private BobberBar? bar;
    private int ticks, inside;

    public RoundLog(IMonitor monitor) => this.monitor = monitor;

    public void Begin(BobberBar newBar)
    {
        Finish();
        bar = newBar;
        ticks = inside = 0;
    }

    public void Tick(in Observation o)
    {
        ticks++;
        if (o.FishInBar)
            inside++;
    }

    /// <summary>Called every game tick; closes the round once the minigame menu is gone.</summary>
    public void CheckFinished()
    {
        if (bar != null && !ReferenceEquals(Game1.activeClickableMenu, bar))
            Finish();
    }

    private void Finish()
    {
        if (bar == null)
            return;
        if (ticks > 0)
        {
            bool caught = bar.distanceFromCatching > 0.9f;
            string result = caught && bar.perfect ? "完美捕获" : caught ? "捕获" : "逃脱";
            string treasure = bar.treasure ? (bar.treasureCaught ? "，宝箱已得" : "，宝箱未得") : "";
            monitor.Log(
                $"本轮：{result}，判定区 {100.0 * inside / ticks:0}%，{ticks / 60.0:0.0}s（难度 {bar.difficulty:0}，类型 {bar.motionType}{treasure}）",
                LogLevel.Info);
        }
        bar = null;
    }
}
