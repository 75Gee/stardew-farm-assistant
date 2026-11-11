using System.Numerics;

namespace FarmAssistant.Fishing.Core;

public enum PlanMode
{
    /// <summary>No chest to take: keep the fish inside (and the perfect catch while it lasts).</summary>
    Track,
    /// <summary>Chest and perfect together: the chest only counts on plans that never let the fish out.</summary>
    Both,
    /// <summary>Chest first: give up the perfect, but never the catch.</summary>
    ChestFirst,
}

/// <summary>
/// Model-predictive choice of the button: roll out ~180 hold/release sequences on the
/// exact bar physics against the predicted fish band and keep the first press of the best.
/// Re-planned every tick, so only the first decision of a plan is ever used.
///
/// The sequence set and the "stays inside" term are the reduced variants: in the
/// simulator they match the full ~1500-sequence / logistic version in catch and perfect
/// rate at about a tenth of the cost (~40 µs per decision).
/// </summary>
public sealed class Planner
{
    private readonly ControllerSettings settings;
    private readonly ulong[] sequences;
    private readonly int[] switches;
    private readonly float[] weights;

    public int Horizon { get; }

    public Planner(ControllerSettings settings)
    {
        this.settings = settings;
        Horizon = Math.Clamp(settings.Horizon, 4, 63);
        sequences = BuildSequences(Horizon);
        switches = new int[sequences.Length];
        ulong pairMask = (1UL << (Horizon - 1)) - 1;
        for (int i = 0; i < sequences.Length; i++)
            switches[i] = BitOperations.PopCount((sequences[i] ^ (sequences[i] >> 1)) & pairMask);
        weights = new float[Horizon];
        float w = 1f;
        for (int i = 0; i < Horizon; i++)
        {
            w *= settings.Discount;
            weights[i] = w;
        }
    }

    /// <summary>
    /// Two segments (hold/release of different lengths) followed by a tail with a fixed
    /// duty cycle; bit k = held on the k-th tick from now.
    /// </summary>
    private static ulong[] BuildSequences(int h)
    {
        int[] first = { 1, 2, 4, 6, 9, 13, 18, 25, 36, 50, 63 };
        int[] second = { 0, 2, 4, 8, 14, 24 };
        int[] duty = { 0, 2, 4 };                 // held ticks out of every 4
        var set = new HashSet<ulong>();
        for (int s0 = 0; s0 < 2; s0++)
        {
            foreach (int l1 in first)
            {
                if (l1 > h) continue;
                foreach (int l2 in second)
                {
                    if (l1 + l2 > h) continue;
                    foreach (int d in duty)
                    {
                        ulong mask = 0;
                        for (int k = 0; k < h; k++)
                        {
                            bool held;
                            if (k < l1) held = s0 == 1;
                            else if (k < l1 + l2) held = s0 == 0;
                            else held = (k - l1 - l2) % 4 < d;
                            if (held) mask |= 1UL << k;
                        }
                        set.Add(mask);
                    }
                }
            }
        }
        return set.ToArray();
    }

    /// <summary>
    /// Best first button state.  <paramref name="fish"/>/<paramref name="band"/> hold the
    /// predicted fish position and band half-width for ticks 0..Horizon (0 = now).
    ///
    /// One roll-out serves the three modes; besides the bar it carries the chest's meter and,
    /// for <see cref="PlanMode.ChestFirst"/>, the expected catch meter:
    /// <list type="bullet">
    /// <item>Track: fish inside, centred, and (while perfect) away from the zone edges.</item>
    /// <item>Both: the same, plus a reward for every tick the chest is inside for as long as
    /// the plan has not let the fish out.</item>
    /// <item>ChestFirst: reward the chest inside and its meter filling, penalise plans that
    /// let the catch meter run low (escape) or fill up before the chest is taken (the round
    /// would end without it).  The perfect term is off.</item>
    /// </list>
    /// With the treasure hunter tackle the game takes nothing away (catch meter, perfect)
    /// while the chest is inside the bar and not yet taken; the roll-out applies the same rule.
    /// </summary>
    public bool Decide(in Observation o, float[] fish, float[] band, PlanMode mode)
    {
        var s = settings;
        int h = Horizon;
        int height = o.BarHeight;
        float halfBar = Math.Max(height / 2f, 1f);
        var tackle = o.Tackle;
        bool chestFirst = mode == PlanMode.ChestFirst;
        bool chest = mode != PlanMode.Track;
        bool hunter = tackle.TreasureHunter > 0;
        float tp = o.TreasurePos;
        float loss = Rules.ProgressLoss(tackle);
        bool perfect = o.Perfect && !chestFirst;
        float distanceWeight = perfect ? s.PerfectDistanceWeight : s.DistanceWeight;
        float perfectWeight = perfect ? s.PerfectWeight : 0f;
        float logFloor = MathF.Log(s.PerfectFloor);
        float fishEndSpeed = fish[h] - fish[h - 1];

        float best = float.MinValue;
        ulong bestMask = 0;
        for (int n = 0; n < sequences.Length; n++)
        {
            ulong mask = sequences[n];
            float pos = o.BarPos, speed = o.BarSpeed, score = 0f;
            float progress = o.Progress, level = o.TreasureLevel;
            bool done = false, alive = true;
            bool inside = o.FishInBar;
            for (int k = 0; k < h; k++)
            {
                float c = fish[k];
                if (k > 0)
                    inside = Rules.InBar(c, pos, height);
                Rules.StepBar(ref pos, ref speed, ((mask >> k) & 1UL) != 0, inside, c, height, tackle);

                // Reward of the next tick, judged with the bar where it now is.
                float c1 = fish[k + 1];
                float sigma = Math.Max(band[k + 1], 0.5f);
                float lo = Rules.ZoneTop(pos);
                float hi = Rules.ZoneBottom(pos, height);
                float mid = (lo + hi) / 2f;
                bool bottom = Rules.BottomRule(c1, pos, height);
                float overlap = bottom ? 1f
                    : Math.Clamp(Math.Min(hi, c1 + sigma) - Math.Max(lo, c1 - sigma), 0f, 2f * sigma) / (2f * sigma);

                // The chest's meter, as the game runs it.
                bool chestIn = false, taken = false;
                if (chest && !done)
                {
                    chestIn = tp >= lo && tp <= hi;
                    level = chestIn ? level + 0.0135f : Math.Max(0f, level - 0.01f);
                    if (level >= 1f)
                        done = taken = true;
                }
                bool sheltered = hunter && chestIn && !done;
                float gain;
                if (chestFirst)
                {
                    // On the way to the chest the fish earns nothing: otherwise a far chest
                    // never outweighs staying put, and the round ends without it.
                    gain = chestIn || done ? s.TreasureFishWeight * overlap : 0f;
                    if (taken)
                        gain += s.TreasureDoneBonus;
                    else if (chestIn)
                        gain += s.TreasureTickWeight;
                    else if (!done)
                        gain -= s.TreasureDistanceWeight * Math.Abs(mid - tp) / halfBar;   // no cap: keep a gradient when far
                    // Expected catch meter: +0.002 inside, -loss outside.
                    progress += overlap * 0.002f - (1f - overlap) * (sheltered ? 0f : loss);
                    if (progress < s.SafeProgress)
                        gain -= s.EscapePenalty * (s.SafeProgress - progress) / s.SafeProgress;
                    if (!done && progress >= 1f)
                        gain -= s.EarlyFinishPenalty;
                }
                else
                {
                    float held = sheltered ? 1f : overlap;
                    gain = held - distanceWeight * Math.Min(Math.Abs(mid - c1) / halfBar, 4f);
                    if (perfectWeight > 0f && !bottom && !sheltered)
                    {
                        // log P(stays inside) ~ -0.077 (3 - m)^2 for a margin m < 3 in logistic
                        // units; tracks log(sigmoid(m)) within ~0.3 on [-3, 3], no exp/log.
                        float m = 1.702f * Math.Min(hi - c1, c1 - lo) / (sigma * s.PerfectSigmaScale);
                        if (m < 3f)
                            gain += perfectWeight * Math.Max(-0.077f * (3f - m) * (3f - m), logFloor);
                    }
                    if (chest)
                    {
                        // Worth something only while this plan still has its perfect catch.
                        if (held < s.BothAliveOverlap)
                            alive = false;
                        if (alive)
                        {
                            if (taken)
                                gain += s.BothDoneBonus;
                            else if (chestIn)
                                gain += s.BothTickWeight * held;
                        }
                    }
                }
                score += weights[k] * gain;
            }
            float endMid = pos + height / 2f - 30f;
            if (chestFirst)
            {
                // Head for the chest while it is pending, otherwise back to the fish.
                float target = done ? fish[h] : tp;
                score -= s.TerminalWeight * weights[h - 1] * Math.Abs(endMid - target) / halfBar;
            }
            else
            {
                score -= s.TerminalWeight * weights[h - 1] *
                         (Math.Abs(endMid - fish[h]) / halfBar + Math.Abs(speed - fishEndSpeed) / 4f);
            }
            score -= s.SwitchCost * switches[n];
            if (score > best)
            {
                best = score;
                bestMask = mask;
            }
        }
        return (bestMask & 1UL) != 0;
    }
}
