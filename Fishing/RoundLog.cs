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
    private string? perfectLost;

    public RoundLog(IMonitor monitor) => this.monitor = monitor;

    public void Begin(BobberBar newBar)
    {
        Finish();
        bar = newBar;
        ticks = inside = 0;
        perfectLost = null;
    }

    public void Tick(in Observation o, PlanMode mode)
    {
        ticks++;
        // 完美在哪一步丢的：主动去取宝箱，还是跟鱼时被甩开。
        if (!o.Perfect && perfectLost == null)
            perfectLost = $"{(mode == PlanMode.ChestFirst ? "取宝箱" : o.TreasureVisible ? "兼顾宝箱时跟丢" : "跟丢")}，第 {ticks / 60.0:0.0}s";
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
            string lost = caught && !bar.perfect && perfectLost != null ? $"，完美丢于{perfectLost}" : "";
            monitor.Log(
                $"本轮：{result}，判定区 {100.0 * inside / ticks:0}%，{ticks / 60.0:0.0}s（难度 {bar.difficulty:0}，类型 {bar.motionType}{treasure}{lost}）",
                LogLevel.Info);
        }
        bar = null;
    }
}
