namespace FarmAssistant.Fishing.Core;

/// <summary>
/// What the controller sees on one tick, taken at the moment the game reads the
/// button (after the fish moved, before the bar moves).
///
/// Deliberately absent: <c>bobberTargetPosition</c> and <c>bobberAcceleration</c>
/// (the fish's hidden intent).  Everything here is either visible on screen
/// (positions, their speeds, whether the fish is inside, the treasure and its catch meter) or a
/// fixed property of the hooked fish and the equipped tackle.
/// </summary>
public readonly record struct Observation(
    float FishPos,
    float FishSpeed,
    float FloaterSinkerAccel,
    int MotionType,
    float Difficulty,
    float BarPos,
    float BarSpeed,
    int BarHeight,
    Tackle Tackle,
    bool FishInBar,
    bool Perfect,
    float Progress,
    bool TreasureVisible,
    float TreasurePos,
    float TreasureLevel = 0f);

public static class MotionTypes
{
    public const int Mixed = 0;
    public const int Dart = 1;
    public const int Smooth = 2;
    public const int Sinker = 3;
    public const int Floater = 4;
}
