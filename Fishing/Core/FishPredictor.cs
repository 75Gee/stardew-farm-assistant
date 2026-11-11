namespace FarmAssistant.Fishing.Core;

/// <summary>
/// Predicts where the fish will be, using only its observed motion.
///
/// The game steers the fish with <c>S += ((T - w) / K - S) / 5</c>, where T is the hidden
/// target and K = Next(10, 30) + (100 - difficulty) is redrawn every tick.  The target is
/// never read; instead each change of the (visible) speed S tells which targets could have
/// produced it: T = w + a * K for one of the 20 possible K.  Intersecting these candidate
/// sets over consecutive ticks narrows down where the fish is heading.  When the speed
/// stops changing the fish has no target and drifts at constant speed.  A candidate set
/// that no longer fits means the fish picked a new target.
///
/// Random re-targeting cannot be foreseen; it is covered by an uncertainty band that
/// widens with look-ahead time and difficulty.
/// </summary>
public sealed class FishPredictor
{
    private const int KCount = 20;               // Next(10, 30)
    private const float MatchTolerance = 0.05f;

    private readonly List<float> candidates = new();
    private readonly List<float> scratch = new();
    private bool hasPrev;
    private float prevPos;
    private float prevSpeed;

    public void Reset()
    {
        candidates.Clear();
        hasPrev = false;
    }

    public void Observe(float pos, float speed, float difficulty)
    {
        if (hasPrev)
        {
            if (speed == prevSpeed)
            {
                // No acceleration this tick: the fish reached its target or has none.
                candidates.Clear();
            }
            else
            {
                // a = (T - w_prev) / K, recovered exactly from S_prev -> S.
                float a = prevSpeed + 5f * (speed - prevSpeed);
                float kOffset = Rules.KOffset(difficulty);
                scratch.Clear();
                for (int k = 0; k < KCount; k++)
                {
                    float t = prevPos + a * (10f + k + kOffset);
                    if (t >= -1.5f && t <= 548.5f)
                        scratch.Add(t);
                }
                if (candidates.Count > 0)
                {
                    int kept = 0;
                    for (int i = 0; i < scratch.Count; i++)
                    {
                        if (Near(candidates, scratch[i]))
                            scratch[kept++] = scratch[i];
                    }
                    // Nothing fits: a new target was chosen, so start over from this tick's set.
                    if (kept > 0)
                        scratch.RemoveRange(kept, scratch.Count - kept);
                }
                candidates.Clear();
                candidates.AddRange(scratch);
            }
        }
        prevPos = pos;
        prevSpeed = speed;
        hasPrev = true;
    }

    private static bool Near(List<float> set, float value)
    {
        foreach (float v in set)
        {
            if (Math.Abs(v - value) <= MatchTolerance)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fills <paramref name="center"/>/<paramref name="half"/> for ticks 0..horizon
    /// (index 0 = now) with the predicted fish position and the half-width of the
    /// band it should stay in, before the re-targeting allowance.
    /// </summary>
    public void Predict(in Observation o, int horizon, float[] center, float[] half, float[] scratchMin, float[] scratchMax)
    {
        float kOffset = Rules.KOffset(o.Difficulty);
        for (int t = 0; t <= horizon; t++)
        {
            scratchMin[t] = float.MaxValue;
            scratchMax[t] = float.MinValue;
        }

        if (candidates.Count == 0)
        {
            Envelope(o, -1f, 1f, horizon, scratchMin, scratchMax);
        }
        else
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float c in candidates)
            {
                lo = Math.Min(lo, c);
                hi = Math.Max(hi, c);
            }
            float kMin = 10f + kOffset, kMax = 29f + kOffset, kMean = 19.5f + kOffset;
            Envelope(o, lo, kMin, horizon, scratchMin, scratchMax);
            Envelope(o, lo, kMax, horizon, scratchMin, scratchMax);
            Envelope(o, hi, kMin, horizon, scratchMin, scratchMax);
            Envelope(o, hi, kMax, horizon, scratchMin, scratchMax);
            Envelope(o, (lo + hi) / 2f, kMean, horizon, scratchMin, scratchMax);
        }

        for (int t = 0; t <= horizon; t++)
        {
            center[t] = (scratchMin[t] + scratchMax[t]) / 2f;
            half[t] = (scratchMax[t] - scratchMin[t]) / 2f;
        }
        center[0] = o.FishPos;
        half[0] = 0f;
    }

    /// <summary>Deterministic roll-out of the fish toward <paramref name="target"/> (-1: none) with a fixed K.</summary>
    private static void Envelope(in Observation o, float target, float k, int horizon, float[] min, float[] max)
    {
        float w = o.FishPos, s = o.FishSpeed, f = o.FloaterSinkerAccel, t = target;
        min[0] = Math.Min(min[0], w);
        max[0] = Math.Max(max[0], w);
        for (int i = 1; i <= horizon; i++)
        {
            if (o.MotionType == MotionTypes.Floater)
                f = Math.Max(f - 0.01f, -1.5f);
            else if (o.MotionType == MotionTypes.Sinker)
                f = Math.Min(f + 0.01f, 1.5f);
            if (t != -1f && Math.Abs(w - t) > 3f)
                s += ((t - w) / k - s) / 5f;
            else
                t = -1f;                    // arrived: keeps drifting at its current speed
            w += s + f;
            if (w > Rules.FishMax) w = Rules.FishMax;
            else if (w < 0f) w = 0f;
            min[i] = Math.Min(min[i], w);
            max[i] = Math.Max(max[i], w);
        }
    }
}
