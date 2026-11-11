namespace FarmAssistant.Fishing.Core;

/// <summary>
/// Planner tuning.  Fixed in the mod: only <see cref="TreasurePriority"/> is a player option;
/// the other values were tuned offline against an exact replica of the minigame.
/// </summary>
public sealed class ControllerSettings
{
    /// <summary>Look-ahead in ticks (60 = 1 s). At most 63.</summary>
    public int Horizon { get; set; } = 36;

    /// <summary>Per-tick discount of future rewards.</summary>
    public float Discount { get; set; } = 0.965f;

    /// <summary>Pull toward the middle of the hit zone (per half bar length).</summary>
    public float DistanceWeight { get; set; } = 0.08f;

    /// <summary>Same while a perfect catch is still possible.</summary>
    public float PerfectDistanceWeight { get; set; } = 0.03f;

    /// <summary>Weight of the "fish stays inside" term while a perfect catch is still possible.</summary>
    public float PerfectWeight { get; set; } = 0.15f;

    public float PerfectFloor { get; set; } = 0.02f;
    public float PerfectSigmaScale { get; set; } = 0.7f;

    /// <summary>End the plan near the fish with a matching speed, so it is not short-sighted.</summary>
    public float TerminalWeight { get; set; } = 0.6f;

    public float SwitchCost { get; set; } = 0.001f;

    /// <summary>Fish band half-width (px) at the first predicted tick.</summary>
    public float FishSigma0 { get; set; } = 2f;

    /// <summary>Band growth (px per tick at difficulty 100) for unforeseeable re-targeting.</summary>
    public float FishSigmaRate { get; set; } = 0.6f;

    /// <summary>
    /// Infer where the fish is heading from how its visible speed changes.  Off: the fish
    /// is only extrapolated at its current speed.
    /// </summary>
    public bool InferTargetFromMotion { get; set; } = true;

    /// <summary>
    /// Treasure first: go for the chest as soon as it appears, even at the cost of a
    /// perfect catch.  Off: only take it when the catch meter is comfortably full.
    /// </summary>
    public bool TreasurePriority { get; set; } = true;

    // Treasure first: the perfect term is dropped while the chest is pending, and the
    // catch is protected by rolling the catch meter forward instead.
    /// <summary>Reward per tick with the chest inside the bar.</summary>
    public float TreasureTickWeight { get; set; } = 1.0f;
    /// <summary>One-off reward when the chest's meter would fill within the plan.</summary>
    public float TreasureDoneBonus { get; set; } = 20f;
    /// <summary>
    /// Pull toward the chest (per half bar length) while it is pending: the chest is often
    /// farther than the bar travels within the look-ahead, so "chest inside" alone is not seen.
    /// </summary>
    public float TreasureDistanceWeight { get; set; } = 0.5f;
    /// <summary>Weight of keeping the fish inside while chasing the chest.</summary>
    public float TreasureFishWeight { get; set; } = 0.5f;
    /// <summary>Catch meter below which the plan is penalised (the fish would escape).</summary>
    public float SafeProgress { get; set; } = 0.12f;
    public float EscapePenalty { get; set; } = 6f;
    /// <summary>Per tick the catch meter would be full while the chest is still pending (the round would end without it).</summary>
    public float EarlyFinishPenalty { get; set; } = 3f;

    /// <summary>
    /// Commit to the chest only when the catch meter can pay for the whole trip: reach the
    /// chest, fill its meter, and get back to the fish, with the fish outside the bar all along
    /// (scaled by BudgetScale) and SafeProgress left over.  Once committed, keep going until
    /// the chest is taken; abort only below AbortProgress.  This stops the bar from shuttling
    /// between fish and chest while the chest meter drains.
    /// </summary>
    public float TreasureBudgetScale { get; set; } = 1.0f;
    /// <summary>Bar travel time ≈ TravelCoef·√distance ticks (measured: ~250 px in ~45 ticks from rest).</summary>
    public float TreasureTravelCoef { get; set; } = 3f;
    /// <summary>Share of the chest-filling time the fish is assumed outside the bar.</summary>
    public float TreasureFillOutside { get; set; } = 0.6f;
    /// <summary>Ticks to get the fish back inside after the chest, at difficulty 0 and per difficulty 100.</summary>
    public float TreasureReacquireTicks { get; set; } = 10f;
    public float TreasureReacquirePerDifficulty { get; set; } = 20f;
    public float TreasureAbortProgress { get; set; } = 0.06f;
    /// <summary>After an abort, the budget must be met with this much extra before committing again.</summary>
    public float TreasureRecommitMargin { get; set; } = 0.1f;

    /// <summary>Treasure when not prioritised: taken only with a comfortable catch meter.</summary>
    public float TreasureWeightNormal { get; set; } = 0.5f;
    public float TreasureMinProgressNormal { get; set; } = 0.5f;
}
