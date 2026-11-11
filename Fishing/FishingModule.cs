using FarmAssistant.Fishing.Core;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace FarmAssistant.Fishing;

/// <summary>
/// 钓鱼助手（原独立模组并入）。开启后：钓鱼小游戏里每一刻由预测控制决定"按住 / 松开"，
/// 替代玩家按键的读数；可选自动上钩。不写入绿条、鱼、进度、宝箱等任何游戏数据。
/// </summary>
internal sealed class FishingModule
{
    internal static FishingModule? Instance { get; private set; }

    private readonly ModEntry mod;

    private readonly PerScreen<FishingController> controller;
    private readonly PerScreen<RoundLog> rounds;
    private readonly PerScreen<ScreenState> screen = new(() => new ScreenState());

    /// <summary>分屏时每个玩家各一份：正在控制的那一局、鱼竿配件、帧计数。</summary>
    internal sealed class ScreenState
    {
        public StardewValley.Menus.BobberBar? Bar;
        public Tackle Tackle;
        public int Frame;                  // 这个分屏每刻 +1（UpdateTicked）
        public int LastFrame = -2;         // 上次替它按键时的 Frame
    }

    public ControllerSettings Settings { get; } = new();
    /// <summary>当前分屏玩家的控制器（分屏时两人同时钓鱼互不干扰）。</summary>
    public FishingController Controller => controller.Value;
    public RoundLog Rounds => rounds.Value;
    internal ScreenState Screen => screen.Value;
    public RodAssist Rod { get; }
    /// <summary>当前分屏玩家是否开着钓鱼助手（由统一快捷键在手持钓竿时开关）。</summary>
    public bool Enabled => mod.FishingOn;

    public ModConfig Config => mod.Config;
    public IMonitor Monitor => mod.Monitor;

    public FishingModule(ModEntry mod, Harmony harmony)
    {
        Instance = this;
        this.mod = mod;
        controller = new PerScreen<FishingController>(() => new FishingController(Settings));
        rounds = new PerScreen<RoundLog>(() => new RoundLog(mod.Monitor));
        Rod = new RodAssist(this);
        BobberBarPatch.Apply(harmony, mod.Monitor);
    }

    /// <summary>开关切换时调用：下一轮从头开始预测。</summary>
    public void Reset() => Controller.Reset();

    /// <summary>每刻游戏更新之后。</summary>
    public void Update()
    {
        Screen.Frame++;
        Rounds.CheckFinished();
        Rod.Update();
    }
}
