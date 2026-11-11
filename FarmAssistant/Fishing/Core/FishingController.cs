namespace FarmAssistant.Fishing.Core;

/// <summary>Per-tick decision: observation in, "hold the button?" out.</summary>
public sealed class FishingController
{
    private readonly ControllerSettings settings;
    private readonly FishPredictor predictor = new();
    private readonly Planner planner;
    private readonly float[] fish, band, scratchMin, scratchMax;

    public FishingController(ControllerSettings? settings = null)
    {
        this.settings = settings ?? new ControllerSettings();
        predictor.InferTarget = this.settings.InferTargetFromMotion;
        planner = new Planner(this.settings);
        int n = planner.Horizon + 1;
        fish = new float[n];
        band = new float[n];
        scratchMin = new float[n];
        scratchMax = new float[n];
    }


    /// <summary>Call when a new minigame starts or control resumes after a gap.</summary>
    public void Reset()
    {
        predictor.Reset();
        committed = false;
        aborted = false;
    }

    private bool committed, aborted;


    /// <summary>
    /// Catch meter the whole chest trip costs: bar to chest, the chest meter filling, back to
    /// the fish and re-acquiring it, all with the fish outside (the hunter's tackle stops the
    /// loss while the chest is inside).
    /// </summary>
    public float TreasureBudget(in Observation o)
    {
        var s = settings;
        float zoneMid = o.BarPos + o.BarHeight / 2f - 30f;
        float go = TravelTicks(Math.Abs(zoneMid - o.TreasurePos));
        float fill = Math.Max(0f, 1f - o.TreasureLevel) / 0.0135f;
        float back = TravelTicks(Math.Abs(o.TreasurePos - o.FishPos))
                     + s.TreasureReacquireTicks + s.TreasureReacquirePerDifficulty * Math.Clamp(o.Difficulty, 0f, 120f) / 100f;
        float outside = go + back + (o.Tackle.TreasureHunter > 0 ? 0f : s.TreasureFillOutside * fill);
        return s.SafeProgress + s.TreasureBudgetScale * outside * Rules.ProgressLoss(o.Tackle);
    }

    private float TravelTicks(float distance) => settings.TreasureTravelCoef * MathF.Sqrt(distance);

    public bool Decide(in Observation o)
    {
        predictor.Observe(o.FishPos, o.FishSpeed, o.Difficulty);
        predictor.Predict(o, planner.Horizon, fish, band, scratchMin, scratchMax);
        float rate = settings.FishSigmaRate * Math.Clamp(o.Difficulty, 0f, 150f) / 100f;
        for (int t = 1; t < band.Length; t++)
            band[t] += settings.FishSigma0 + rate * t;

        TreasureGoal goal = TreasureGoal.None;
        if (o.TreasureVisible && settings.TreasurePriority)
        {
            // The catch comes first: commit only when the meter pays for the whole trip, then
            // see it through instead of turning back half way (the chest meter drains when left).
            if (committed && o.Progress < settings.TreasureAbortProgress)
            {
                committed = false;
                aborted = true;
            }
            else if (!committed)
            {
                float need = TreasureBudget(o) + (aborted ? settings.TreasureRecommitMargin : 0f);
                if (o.Progress >= need)
                    committed = true;
            }
            goal = committed ? TreasureGoal.Priority : TreasureGoal.None;
        }
        else if (o.TreasureVisible && o.Progress >= settings.TreasureMinProgressNormal)
        {
            goal = TreasureGoal.Opportunistic;
        }
        return planner.Decide(o, fish, band, goal);
    }
}
