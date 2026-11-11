using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace FarmAssistant;

/// <summary>规划器看到的地图：只读。</summary>
internal interface IFieldView
{
    int Width { get; }
    int Height { get; }
    int Ticks { get; }
    /// <summary>这格是不是耕地（浇水模式下含花盆）：用来判断一片地连到哪里。</summary>
    bool IsSoil(int x, int y);
    /// <summary>这格还要处理：浇水模式 = 未浇水的作物；播种模式 = 能种下手里这种种子的空耕地。</summary>
    bool IsTarget(Point tile);
    /// <summary>农夫能否站在这格（静态障碍）。</summary>
    bool IsPassable(int x, int y);
    bool CanRefill(int x, int y);
}

internal enum FieldMode { Water, Seed }

/// <summary>游戏地图的实现，只读取游戏状态。</summary>
internal sealed class GameFieldView : IFieldView
{
    private readonly GameLocation loc;
    private readonly FieldMode mode;
    private readonly bool seedInSeason;

    /// <summary>播种模式下：手里这种种子现在能不能种在这里（季节、室内等）。</summary>
    public bool SeedInSeason => seedInSeason;

    /// <summary>播种模式下：这种种子种下后那格是否可能不能走（藤架作物；混合种子可能出藤架作物）。</summary>
    public bool SeedIsRaised { get; }

    public GameFieldView(GameLocation location, FieldMode mode = FieldMode.Water, string? seedItemId = null)
    {
        loc = location;
        this.mode = mode;
        if (mode == FieldMode.Seed && seedItemId != null)
            (seedInSeason, SeedIsRaised) = SeedRules(location, seedItemId);
        var layer = location.Map.Layers[0];
        Width = layer.LayerWidth;
        Height = layer.LayerHeight;
    }

    public int Width { get; }
    public int Height { get; }
    public int Ticks => Game1.ticks;

    /// <summary>该格是需要浇水、今天还没浇的耕地（含室内花盆）。</summary>
    public static bool IsTarget(GameLocation location, Point tile)
    {
        var v = new Vector2(tile.X, tile.Y);
        if (location.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt dirt)
            return dirt.needsWatering() && !dirt.isWatered();
        if (location.objects.TryGetValue(v, out var obj) && obj is IndoorPot pot && pot.hoeDirt.Value is { } potDirt)
            return potDirt.needsWatering() && !potDirt.isWatered();
        return false;
    }

    /// <summary>该格是能播种的空耕地（不含花盆：往花盆里放种子走的是另一条交互）。</summary>
    public static bool IsEmptySoil(GameLocation location, Point tile)
    {
        var v = new Vector2(tile.X, tile.Y);
        return location.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt { crop: null }
               && !location.objects.ContainsKey(v);
    }

    public bool IsTarget(Point tile) => mode == FieldMode.Water
        ? IsTarget(loc, tile)
        : seedInSeason && IsEmptySoil(loc, tile);

    /// <summary>离 from 最近（曼哈顿距离不超过 maxDistance）的待处理格。</summary>
    public Point? NearestTarget(Point from, int maxDistance)
    {
        Point? best = null;
        int bestDistance = maxDistance + 1;
        void Consider(Vector2 v)
        {
            var p = new Point((int)v.X, (int)v.Y);
            int d = Math.Abs(p.X - from.X) + Math.Abs(p.Y - from.Y);
            if (d < bestDistance && IsTarget(p))
            {
                bestDistance = d;
                best = p;
            }
        }
        foreach (var pair in loc.terrainFeatures.Pairs)
            if (pair.Value is HoeDirt)
                Consider(pair.Key);
        if (mode == FieldMode.Water)
            foreach (var pair in loc.objects.Pairs)
                if (pair.Value is IndoorPot)
                    Consider(pair.Key);
        return best;
    }

    public bool IsSoil(int x, int y)
    {
        var v = new Vector2(x, y);
        if (loc.terrainFeatures.TryGetValue(v, out var tf) && tf is HoeDirt)
            return true;
        return mode == FieldMode.Water && loc.objects.TryGetValue(v, out var obj) && obj is IndoorPot { hoeDirt.Value: not null };
    }

    /// <summary>
    /// 与 HoeDirt.canPlantThisSeedHere 相同的季节规则，但不调用它：那个方法对混合种子会掷随机数，
    /// 种错季节时还会弹提示。混合种子按"当季总能种"处理。
    /// Raised = 种下后那格可能不能走：混合种子按春季随机出的作物里有青豆（藤架）来算，
    /// 只要可能按春季出（春天，或室内、姜岛等不分季节的地方）就当作藤架作物，宁可慢一点也不把路堵死。
    /// </summary>
    private static (bool InSeason, bool Raised) SeedRules(GameLocation location, string itemId)
    {
        if (itemId == "MixedFlowerSeeds")
            return (true, false);
        if (itemId == "770")
            return (true, !location.IsOutdoors || location.SeedsIgnoreSeasonsHere()
                          || location.GetSeason() is Season.Spring or Season.Winter);
        if (!Crop.TryGetData(itemId, out var data) || data.Seasons.Count == 0)
            return (false, false);
        bool inSeason = !location.IsOutdoors || location.SeedsIgnoreSeasonsHere()
                        || data.Seasons.Contains(location.GetSeason()) || itemId is "309" or "310" or "311";
        return (inSeason, data.IsRaised);
    }

    public bool IsPassable(int x, int y)
    {
        // 走上去会切换地图的格子一律不走。
        foreach (var warp in loc.warps)
            if (warp.X == x && warp.Y == y)
                return false;
        if (loc.doesTileHaveProperty(x, y, "TouchAction", "Back") != null)
            return false;
        // 与农夫走路时同一个碰撞判定，矩形是站在格子中央时的碰撞箱；不触发碰撞效果。
        var box = new Rectangle(x * 64 + 8, y * 64 + 16, 48, 32);
        if (!loc.isCollidingPosition(box, Game1.viewport, isFarmer: true, damagesFarmer: 0, glider: false,
                character: Game1.player, pathfinding: false, projectile: false, ignoreCharacterRequirement: false,
                skipCollisionEffects: true))
            return true;
        // 这个判定也算 NPC（含宠物、马）和牧场动物。它们会走开，结果又会被缓存，
        // 所以只是被它们占着的格子当作可走；真挡路时，走路的卡住检测会临时封住这一格。
        return OccupiedByCharacter(box);
    }

    private bool OccupiedByCharacter(Rectangle box)
    {
        foreach (var npc in loc.characters)
            if (npc.GetBoundingBox().Intersects(box))
                return true;
        foreach (var animal in loc.animals.Values)
            if (animal.GetBoundingBox().Intersects(box))
                return true;
        return false;
    }

    public bool CanRefill(int x, int y) => loc.CanRefillWateringCanOnTile(x, y);
}
