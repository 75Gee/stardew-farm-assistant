using FarmAssistant.Fishing;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Tools;

namespace FarmAssistant;

/// <summary>
/// 农场助手（含钓鱼助手）。统一快捷键按"当前手持物"触发，鼠标右键完全保留原版行为：
/// 钓竿 → 开关钓鱼助手；水壶 → 浇完最近的一片；种子 → 种满最近的一片；
/// 斧头 → 把最近的一片（可配置为整张地图）长成的树、树桩、木垛、大树桩 / 大木头按规划好的顺序砍完并捡掉落物。
/// 只模拟玩家操作，不修改任何游戏数据；按任意键即可接管。
/// </summary>
public sealed class ModEntry : Mod
{
    private const string GmcmId = "spacechase0.GenericModConfigMenu";
    private const string IconItem = "(T)WateringCan";

    private readonly PerScreen<AutoWaterer> waterer;
    private readonly PerScreen<SeedSower> sower;
    private readonly PerScreen<TreeChopper> chopper;
    private readonly PerScreen<bool> fishingOn = new();
    private FishingModule fishing = null!;

    internal ModConfig Config { get; private set; } = null!;

    /// <summary>当前分屏玩家的三个执行器。</summary>
    private IEnumerable<AutoTask> Tasks => new AutoTask[] { waterer.Value, sower.Value, chopper.Value };

    public ModEntry()
    {
        waterer = new PerScreen<AutoWaterer>(() => new AutoWaterer(this));
        sower = new PerScreen<SeedSower>(() => new SeedSower(this));
        chopper = new PerScreen<TreeChopper>(() => new TreeChopper(this));
    }

    public override void Entry(IModHelper helper)
    {
        Config = helper.ReadConfig<ModConfig>();
        var harmony = new Harmony(ModManifest.UniqueID);
        VirtualInput.Apply(harmony);
        fishing = new FishingModule(this, harmony);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += (_, _) => fishingOn.Value = false;   // 每次进存档都从原版状态开始
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.Input.ButtonsChanged += OnButtonsChanged;
        helper.Events.GameLoop.UpdateTicking += (_, _) => Run(() =>
        {
            foreach (var task in Tasks)
                task.Tick();
        });
        helper.Events.GameLoop.UpdateTicked += (_, _) => fishing.Update();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => StopAll();
        helper.Events.Player.Warped += (_, e) => { if (e.IsLocalPlayer) StopAll(); };
    }

    // ---------- 配置菜单 ----------

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GmcmId);
        if (gmcm == null)
            return;
        gmcm.Register(ModManifest,
            reset: () => { Config = new ModConfig(); fishing.ApplyConfig(); },
            save: () => { Helper.WriteConfig(Config); fishing.ApplyConfig(); });

        gmcm.AddKeybindList(ModManifest, () => Config.ToggleKey, v => Config.ToggleKey = v,
            () => "快捷键",
            () => "按当前手持物触发：钓竿 = 开关钓鱼助手，水壶 = 浇最近的一片，种子 = 种满最近的一片，斧头 = 按顺序砍完最近的一片树和木垛。进行中再按一次停止。macOS 上单按 F8 是媒体键，需 fn+F8。");

        gmcm.AddSectionTitle(ModManifest, () => "砍伐");
        gmcm.AddBoolOption(ModManifest, () => Config.ChopWholeMap, v => Config.ChopWholeMap = v,
            () => "砍整张地图", () => "打开：砍当前地图上所有长成的树、树桩、木垛。关闭（默认）：只砍离自己最近（30 格内）的那一片，相距 4 格以内的算连着。");
        gmcm.AddBoolOption(ModManifest, () => Config.CollectDrops, v => Config.CollectDrops = v,
            () => "砍完捡掉落物", () => "砍完后走到能把掉落物吸过来的地方；走不到吸附范围内的会跳过。");

        gmcm.AddSectionTitle(ModManifest, () => "钓鱼");
        gmcm.AddBoolOption(ModManifest, () => Config.TreasurePriority, v => Config.TreasurePriority = v,
            () => "宝箱优先", () => "进度够付整趟取箱时才去拿，拿时一口气拿完，可能因此失去完美；关闭则只在进度较高时顺手拿。");
        gmcm.AddBoolOption(ModManifest, () => Config.AutoHook, v => Config.AutoHook = v,
            () => "自动上钩", () => "鱼咬钩时自动提竿。");
    }

    // ---------- 输入 ----------

    /// <summary>当前分屏玩家是否开着钓鱼助手。</summary>
    internal bool FishingOn => fishingOn.Value;

    private const string NeedKeys = "自动操作需要在游戏设置里给上下左右移动都绑定键盘按键";
    private const int SearchRange = 30;   // 浇水 / 播种：在自己周围这么多格内找最近的那片地

    /// <summary>统一快捷键：按当前手持物触发对应的功能。</summary>
    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!Context.IsWorldReady || !Config.ToggleKey.JustPressed())
            return;
        Helper.Input.SuppressActiveKeybinds(Config.ToggleKey);   // 不让 F 等按键再触发游戏功能
        var player = Game1.player;
        switch (Features.ForHeld(player))
        {
            case Feature.Fish:
                fishingOn.Value = !fishingOn.Value;
                fishing.Reset();
                Notify($"钓鱼助手：{(fishingOn.Value ? "开启" : "关闭")}", Features.Icon(Feature.Fish, player));
                break;
            case Feature.Water:
                Run(() => StartWatering(player));
                break;
            case Feature.Seed:
                Run(() => StartSeeding(player));
                break;
            case Feature.Chop:
                Run(() => StartChopping(player));
                break;
            default:
                Notify("手持钓竿、水壶、种子或斧头时按快捷键");
                break;
        }
    }

    private bool CanStart(Farmer player)
    {
        if (!Context.IsPlayerFree || player.UsingTool || player.isRidingHorse())
            return false;
        if (VirtualInput.MovementBound())
            return true;
        Notify(NeedKeys);
        return false;
    }

    private void StartWatering(Farmer player)
    {
        if (waterer.Value.Active)
        {
            waterer.Value.Stop("已停止自动浇水");
            return;
        }
        if (!CanStart(player))
            return;
        var view = new GameFieldView(player.currentLocation);
        if (view.NearestTarget(player.TilePoint, SearchRange) is Point start)
            waterer.Value.Start(start);
        else
            Notify("附近没有需要浇水的作物", Features.Icon(Feature.Water, player));
    }

    private void StartSeeding(Farmer player)
    {
        if (sower.Value.Active)
        {
            sower.Value.Stop("已停止自动播种");
            return;
        }
        if (!CanStart(player))
            return;
        string icon = Features.Icon(Feature.Seed, player);
        var view = new GameFieldView(player.currentLocation, FieldMode.Seed, player.ActiveObject!.ItemId);
        if (!view.SeedInSeason)
            Notify("这种种子现在不能种在这里", icon);
        else if (view.NearestTarget(player.TilePoint, SearchRange) is Point start)
            sower.Value.Start(start);
        else
            Notify("附近没有空着的耕地", icon);
    }

    /// <summary>找出当前地图里所有该砍的目标，自己规划顺序砍完；进行中再按一次停止。</summary>
    private void StartChopping(Farmer player)
    {
        if (chopper.Value.Active)
        {
            chopper.Value.Stop("已停止自动砍伐");
            return;
        }
        if (CanStart(player))
            chopper.Value.StartAll(player.currentLocation, (Axe)player.CurrentTool, Config.ChopWholeMap, SearchRange);
    }

    /// <summary>玩家自己按了任何键（快捷键本身除外）：立即交还控制。</summary>
    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || IsToggleButton(e.Button))
            return;
        foreach (var task in Tasks)
            if (task.Active)
            {
                task.TakeOver();
                return;
            }
    }

    private bool IsToggleButton(SButton button)
    {
        foreach (var keybind in Config.ToggleKey.Keybinds)
            if (Array.IndexOf(keybind.Buttons, button) >= 0)
                return true;
        return false;
    }

    private void StopAll()
    {
        foreach (var task in Tasks)
            task.Stop();
        VirtualInput.Clear();
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Monitor.Log($"农场助手出错，已停止：{ex}", LogLevel.Error);
            try { StopAll(); } catch { /* ignore */ }
        }
    }

    internal void Notify(string text, string icon = IconItem)
    {
        Monitor.Log(text, LogLevel.Info);
        if (!Context.IsWorldReady)
            return;
        var message = new HUDMessage(text);
        try
        {
            message.messageSubject = ItemRegistry.Create(icon);
        }
        catch
        {
            // 图标只是装饰
        }
        Game1.addHUDMessage(message);
    }
}
