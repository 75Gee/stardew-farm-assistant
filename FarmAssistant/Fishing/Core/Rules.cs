namespace FarmAssistant.Fishing.Core;

/// <summary>
/// The fishing minigame's rules, copied expression for expression from
/// <c>StardewValley.Menus.BobberBar.update</c> (1.6) so float rounding matches the game.
/// Positions are the game's own: <c>bobberPosition</c> (fish, 0..532) and
/// <c>bobberBarPos</c> (bar top, 0..568-height), y grows downward.
/// </summary>
public static class Rules
{
    public const float FishMax = 532f;

    /// <summary><c>bobberInBar</c>: the fish counts as inside the bar.</summary>
    public static bool InBar(float fish, float barPos, int barHeight)
    {
        if (fish + 12f <= barPos - 32f + (float)barHeight && fish - 16f >= barPos - 32f)
            return true;
        return BottomRule(fish, barPos, barHeight);
    }

    /// <summary>Lowest fish position that is inside (top edge of the hit zone).</summary>
    public static float ZoneTop(float barPos) => barPos - 16f;

    /// <summary>Highest fish position that is inside (bottom edge of the hit zone).</summary>
    public static float ZoneBottom(float barPos, int barHeight) => barPos + barHeight - 44f;

    /// <summary>The bottom special case: fish deep enough and bar resting near the bottom.</summary>
    public static bool BottomRule(float fish, float barPos, int barHeight)
        => fish >= (float)(548 - barHeight) && barPos >= (float)(568 - barHeight - 4);


    /// <summary>
    /// One tick of bar physics, after <c>buttonPressed</c> has been decided.
    /// <paramref name="inBar"/> is this tick's <c>bobberInBar</c> (computed before the bar moves).
    /// </summary>
    public static void StepBar(ref float pos, ref float speed, bool held, bool inBar, float fish, int height, in Tackle tackle)
    {
        float gravity = held ? -0.25f : 0.25f;
        if (held && gravity < 0f && (pos == 0f || pos == (float)(568 - height)))
            speed = 0f;
        if (inBar)
        {
            gravity *= tackle.Barbed > 0 ? 0.3f : 0.6f;
            for (int i = 0; i < tackle.Barbed; i++)
            {
                if (fish + 16f < pos + (float)(height / 2))
                    speed -= i > 0 ? 0.05f : 0.2f;
                else
                    speed += i > 0 ? 0.05f : 0.2f;
                if (i > 0)
                    gravity *= 0.9f;
            }
        }
        speed += gravity;
        pos += speed;
        if (pos + (float)height > 568f)
        {
            pos = 568 - height;
            speed = (0f - speed) * 2f / 3f * (tackle.Lead > 0 ? (float)tackle.Lead * 0.1f : 1f);
        }
        else if (pos < 0f)
        {
            pos = 0f;
            speed = (0f - speed) * 2f / 3f;
        }
    }

    /// <summary>Progress lost per tick while the fish is outside (trap bobber reduces it).</summary>
    public static float ProgressLoss(in Tackle tackle, bool beginnersRod = false, float penaltyModifier = 1f)
    {
        if (tackle.Trap > 0)
        {
            float reduction = 0.003f;
            float amount = 0.001f;
            for (int i = 0; i < tackle.Trap; i++)
            {
                reduction -= amount;
                amount /= 2f;
            }
            return Math.Max(0.001f, reduction) * penaltyModifier;
        }
        return (beginnersRod ? 0.002f : 0.003f) * penaltyModifier;
    }

    /// <summary>The fish's commanded-speed divisor is <c>Next(10, 30) + KOffset</c>.</summary>
    public static float KOffset(float difficulty) => 100f - Math.Min(100f, difficulty);
}
