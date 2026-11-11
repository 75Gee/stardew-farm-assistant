using StardewValley;
using StardewValley.Tools;
using Object = StardewValley.Object;

namespace FarmAssistant;

/// <summary>按手持物区分的功能。统一快捷键触发当前手持物对应的那一项。</summary>
internal enum Feature { Fish, Water, Seed, Chop }

internal static class Features
{
    /// <summary>当前手持物对应的功能；手里不是钓竿、水壶、斧头或种子时为 null。</summary>
    public static Feature? ForHeld(Farmer player) => player.CurrentTool switch
    {
        FishingRod => Feature.Fish,
        WateringCan => Feature.Water,
        Axe => Feature.Chop,
        _ => player.ActiveObject is { Category: Object.SeedsCategory } ? Feature.Seed : null,
    };


    /// <summary>提示框里的图标。播种用手里那种种子。</summary>
    public static string Icon(Feature f, Farmer player) => f switch
    {
        Feature.Fish => "(T)BambooPole",
        Feature.Water => "(T)WateringCan",
        Feature.Chop => "(T)Axe",
        _ => player.ActiveObject?.QualifiedItemId ?? "(O)472",
    };
}
