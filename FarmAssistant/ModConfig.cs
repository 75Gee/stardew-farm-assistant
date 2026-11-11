using StardewModdingAPI.Utilities;

namespace FarmAssistant;

/// <summary>玩家配置。装了 Generic Mod Config Menu 时可在游戏内修改，否则编辑 config.json。</summary>
public sealed class ModConfig
{
    /// <summary>
    /// 快捷键（SMAPI 按键写法，逗号分隔多个）：按当前手持物触发——钓竿 = 开关钓鱼助手，
    /// 水壶 = 浇最近的一片，种子 = 种满最近的一片，斧头 = 按顺序砍完最近的一片树和木垛；进行中再按一次停止。
    /// macOS 默认把 F1–F12 当媒体键，单按 F8 游戏收不到（需 fn+F8），所以默认同时提供 Ctrl+F。
    /// </summary>
    public KeybindList ToggleKey { get; set; } = KeybindList.Parse("LeftControl + F, F8");

    // ---------- 砍伐 ----------

    /// <summary>砍整张地图（默认只砍离自己最近的那一片，免得把农场上想留的大树一起砍掉）。</summary>
    public bool ChopWholeMap { get; set; } = false;

    /// <summary>砍完后去捡掉落物（走不到吸附范围内的会跳过）。</summary>
    public bool CollectDrops { get; set; } = true;

    // ---------- 钓鱼 ----------

    /// <summary>宝箱优先：进度够付整趟取箱才去拿，拿时一口气拿完（可能因此失去完美）；关闭则只在进度较高时顺手拿。</summary>
    public bool TreasurePriority { get; set; } = true;

    /// <summary>自动上钩：鱼咬钩时自动提竿。</summary>
    public bool AutoHook { get; set; } = false;
}
