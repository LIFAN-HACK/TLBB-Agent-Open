using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public enum HangReturnMethod
{
    EarthSpirit = 0,
    AutoNavigation = 1
}

public sealed record HangReturnMethodOption(
    HangReturnMethod Value,
    string Name,
    bool IsAvailable,
    bool ShowPendingBadge);

public sealed record HangCombatModeOption(
    CombatBackendPreference Value,
    string Name,
    bool IsAvailable,
    bool ShowPendingBadge);

public sealed record SkillExecutionModeOption(bool UseAi, string Name)
{
    public override string ToString() => Name;
}

public sealed record AiSkillSlotViewModel(
    ImageSource? Preview,
    string SkillName,
    string ActionText,
    string CooldownText,
    string ScoreText,
    string SlotText);

public sealed partial class HangPageViewModel : PageViewModel
{
    private readonly ModuleDescriptor _descriptor;
    private readonly IAutomationController? _controller;
    private readonly IGlobalConfigService _global;
    private readonly IReturnPointService _returnPoints;
    private readonly Action _openGlobalConfig;
    private readonly IWindowBindingService? _binding;
    private readonly ILatestFrameService? _latestFrames;
    private readonly IPerceptionHub? _perceptionHub;
    private readonly IGameHotkeyProfileService? _gameHotkeys;
    private readonly DispatcherTimer _aiSkillPreviewTimer = new();

    public HangPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        ReturnPointPageViewModel returnPoint,
        IGlobalConfigService global,
        IReturnPointService returnPoints,
        Action openGlobalConfig,
        IWindowBindingService? binding = null,
        ILatestFrameService? latestFrames = null,
        IPerceptionHub? perceptionHub = null,
        IGameHotkeyProfileService? gameHotkeys = null)
    {
        _descriptor = descriptor;
        _controller = controller;
        _global = global;
        _returnPoints = returnPoints;
        _openGlobalConfig = openGlobalConfig;
        _binding = binding;
        _latestFrames = latestFrames;
        _perceptionHub = perceptionHub;
        _gameHotkeys = gameHotkeys;
        ReturnPoint = returnPoint;

        if (_controller is not null)
        {
            _controller.StateChanged += (_, _) => UiThread.Dispatch(() =>
            {
                OnPropertyChanged(nameof(CurrentStateName));
                OnPropertyChanged(nameof(RunState));
                OnPropertyChanged(nameof(CanChangeMode));
                OnPropertyChanged(nameof(BuiltInMonitorStatus));
                OnPropertyChanged(nameof(ConfigSummary));
                OnPropertyChanged(nameof(IsConfigComplete));
                OnPropertyChanged(nameof(ReadySummary));
                OnPropertyChanged(nameof(IsReady));
            });
        }

        _global.Changed += (_, _) => UiThread.Dispatch(RefreshSummary);
        _returnPoints.Changed += (_, _) => UiThread.Dispatch(RefreshSummary);
        _aiSkillPreviewTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _aiSkillPreviewTimer.Tick += (_, _) => RefreshAiSkillPreview();
        _aiSkillPreviewTimer.Start();
    }

    public override string Title => "挂机打怪";
    public override string Subtitle => "";

    public ReturnPointPageViewModel ReturnPoint { get; }
    public string Priority => _descriptor.Priority;
    public ModuleIntegrationStatus Status => _descriptor.Status;
    public bool HasController => _controller is not null;
    public string CurrentStateName => _controller?.CurrentStateName ?? "待启动";
    public RunState RunState => _controller?.RunState ?? RunState.Idle;

    public IReadOnlyList<HangCombatModeOption> CombatModes { get; } =
        new[]
        {
            new HangCombatModeOption(CombatBackendPreference.InternalPlugin, "内置挂机", false, true),
            new HangCombatModeOption(CombatBackendPreference.ExternalOcr, "按键打怪", true, false)
        };

    public IReadOnlyList<HangReturnMethodOption> ReturnMethods { get; } =
        new[]
        {
            new HangReturnMethodOption(HangReturnMethod.EarthSpirit, "土灵珠返点", true, false),
            new HangReturnMethodOption(HangReturnMethod.AutoNavigation, "自动寻路", false, true)
        };

    public IReadOnlyList<SkillExecutionModeOption> SkillModes { get; } =
    [
        new(true, "AI 自动判定"),
        new(false, "自设按键")
    ];

    public HangCombatModeOption SelectedCombatMode
    {
        get
        {
            var value = _global.Current.CombatBackendPreference == CombatBackendPreference.InternalPlugin
                ? CombatBackendPreference.InternalPlugin
                : CombatBackendPreference.ExternalOcr;
            return CombatModes.First(mode => mode.Value == value);
        }
        set
        {
            if (value is null
                || !value.IsAvailable
                || SelectedCombatMode.Value == value.Value)
            {
                return;
            }

            _global.Current.CombatBackendPreference = value.Value;
            if (value.Value == CombatBackendPreference.InternalPlugin)
            {
                // 正式界面只支持快捷键方式维持内挂，不再暴露旧坐标触发。
                _global.Current.GameBuiltInHangSwitchMode = GameBuiltInHangSwitchMode.Hotkey;
            }
            else
            {
                ApplyDefaultTargetSwitching();
            }

            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    public bool IsBuiltInMode => SelectedCombatMode.Value == CombatBackendPreference.InternalPlugin;
    public bool IsKeyMode => !IsBuiltInMode;
    public bool IsEmei => _global.Current.School == School.Emei;
    public bool CanChangeMode => !IsActiveState(RunState);

    public string BuiltInMonitorStatus => "未就绪";

    public string BuiltInHotkeySummary => string.IsNullOrWhiteSpace(_global.Current.GameBuiltInHangSwitchHotkey)
        ? "快捷键未配置"
        : _global.Current.GameBuiltInHangSwitchHotkey;

    public bool AttackPlayers
    {
        get => !_global.Current.Targeting.BlockPlayerTargets;
        set
        {
            _global.Current.Targeting.BlockPlayerTargets = !value;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    public SkillExecutionModeOption SelectedSkillMode
    {
        get => SkillModes.First(mode => mode.UseAi == _global.Current.SkillCooldownRecognitionEnabled);
        set
        {
            if (value is null || _global.Current.SkillCooldownRecognitionEnabled == value.UseAi)
                return;

            _global.Current.SkillCooldownRecognitionEnabled = value.UseAi;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAiSkillMode));
            OnPropertyChanged(nameof(IsCustomSkillMode));
            OnPropertyChanged(nameof(SkillModeText));
            OnPropertyChanged(nameof(SkillModeDetail));
            OnPropertyChanged(nameof(SkillSummary));
            _global.NotifyChanged();
            RefreshAiSkillPreview();
        }
    }

    public bool IsAiSkillMode => _global.Current.SkillCooldownRecognitionEnabled;
    public bool IsCustomSkillMode => !IsAiSkillMode;

    public string SkillModeText => IsAiSkillMode ? "AI 自动判定" : "自设按键";

    public string SkillModeDetail => IsAiSkillMode
        ? "读取当前技能槽截图、技能身份和 CD，只释放已识别且就绪的技能。"
        : "按玩家填写的快捷键与间隔循环释放，遵守公共 CD。";

    public ObservableCollection<AiSkillSlotViewModel> AiSkillSlots { get; } = new();

    public string AiSkillInventoryStatus { get; private set; } = "等待 AI 识别技能槽。";

    public bool EnableSelfHeal
    {
        get => IsEmei && _global.Current.EnableEmHeal;
        set
        {
            if (!IsEmei)
                return;

            _global.Current.EnableEmHeal = value;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    public bool AutoReturnEnabled
    {
        get => _returnPoints.Current.EnableReturnPoint
               && _returnPoints.Current.EnableEarthSpiritReturn;
        set
        {
            var config = _returnPoints.Current;
            if (value)
            {
                config.EnableReturnPoint = true;
                config.EnableEarthSpiritReturn = true;
                config.EnableFixedFarmReturn = false;
            }
            else
            {
                config.EnableEarthSpiritReturn = false;
                config.EnableFixedFarmReturn = false;
                if (!config.EnableDeathReturn && !config.EnableStuckRecovery)
                    config.EnableReturnPoint = false;
            }

            OnPropertyChanged();
            _returnPoints.NotifyChanged();
        }
    }

    public HangReturnMethodOption SelectedReturnMethod
    {
        get => ReturnMethods[0];
        set
        {
            if (value is null || !value.IsAvailable || value.Value != HangReturnMethod.EarthSpirit)
                return;

            OnPropertyChanged();
        }
    }

    public string EarthSpiritHotkey
    {
        get => _global.Current.EarthSpiritHotkey;
        set
        {
            _global.Current.EarthSpiritHotkey = value?.Trim() ?? "";
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    public string AutoReturnStatus => !AutoReturnEnabled
        ? "未开启"
        : string.IsNullOrWhiteSpace(EarthSpiritHotkey) ? "缺少快捷键" : "已开启";

    // 兼容诊断工具：仅输出面向用户的状态，不再包含间隔、坐标等技术参数。
    public string AttackSummary => IsBuiltInMode
        ? "由内置挂机执行"
        : string.IsNullOrWhiteSpace(_global.Current.AttackHotkey) ? "未配置" : "已配置";

    public string AcquireSummary => IsBuiltInMode
        ? "由内置挂机执行"
        : string.IsNullOrWhiteSpace(_global.Current.Targeting.AcquireHotkey) ? "未配置" : "自动切怪";

    public string SkillSummary
    {
        get
        {
            if (IsBuiltInMode)
                return "由内置挂机执行";

            if (IsAiSkillMode)
            {
                var mapped = _global.Current.AiSkills.Count(s =>
                    s.Enabled && !string.IsNullOrWhiteSpace(s.ExpectedVisualSkillId));
                return mapped == 0
                    ? "AI 自动 · 等待识别并导入"
                    : $"AI 自动 · 已导入 {mapped} 个";
            }

            var enabled = _global.Current.Skills.Count(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Hotkey));
            return enabled == 0 ? "未配置" : $"定时循环 · {enabled} 个";
        }
    }

    public string RecoverySummary => IsBuiltInMode ? "由内置挂机执行" : "沿用角色通用配置";
    public string TargetSummary => IsBuiltInMode ? "内置挂机监控" : "按键打怪";
    public string ReturnPointSummary => AutoReturnEnabled ? "已开启 · 土灵珠" : "未开启";

    public bool IsConfigComplete
    {
        get
        {
            var config = _global.Current;
            if (IsBuiltInMode)
                return !string.IsNullOrWhiteSpace(config.GameBuiltInHangSwitchHotkey);

            if (string.IsNullOrWhiteSpace(config.AttackHotkey)
                && !config.Skills.Any(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Hotkey)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(config.Targeting.AcquireHotkey))
                return false;

            if (AutoReturnEnabled && string.IsNullOrWhiteSpace(config.EarthSpiritHotkey))
                return false;

            return true;
        }
    }

    public string ConfigSummary => IsConfigComplete ? "配置完整" : "待配置";

    // 保留诊断工具所依赖的旧属性名；语义仅表示本地配置完整度。
    public string ReadySummary => ConfigSummary;
    public bool IsReady => IsConfigComplete;

    [RelayCommand]
    private void OpenGlobalConfig() => _openGlobalConfig();

    private bool ApplyDefaultTargetSwitching()
    {
        var targeting = _global.Current.Targeting;
        var changed = false;
        if (!targeting.AutoAcquireWhenNoTarget)
        {
            targeting.AutoAcquireWhenNoTarget = true;
            changed = true;
        }

        if (targeting.SwitchMode != TargetSwitchMode.KillThenSwitch)
        {
            targeting.SwitchMode = TargetSwitchMode.KillThenSwitch;
            changed = true;
        }

        return changed;
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(SelectedCombatMode));
        OnPropertyChanged(nameof(IsBuiltInMode));
        OnPropertyChanged(nameof(IsKeyMode));
        OnPropertyChanged(nameof(IsEmei));
        OnPropertyChanged(nameof(BuiltInMonitorStatus));
        OnPropertyChanged(nameof(BuiltInHotkeySummary));
        OnPropertyChanged(nameof(AttackPlayers));
        OnPropertyChanged(nameof(SelectedSkillMode));
        OnPropertyChanged(nameof(IsAiSkillMode));
        OnPropertyChanged(nameof(IsCustomSkillMode));
        OnPropertyChanged(nameof(SkillModeText));
        OnPropertyChanged(nameof(SkillModeDetail));
        OnPropertyChanged(nameof(EnableSelfHeal));
        OnPropertyChanged(nameof(AutoReturnEnabled));
        OnPropertyChanged(nameof(SelectedReturnMethod));
        OnPropertyChanged(nameof(EarthSpiritHotkey));
        OnPropertyChanged(nameof(AutoReturnStatus));
        OnPropertyChanged(nameof(AttackSummary));
        OnPropertyChanged(nameof(AcquireSummary));
        OnPropertyChanged(nameof(SkillSummary));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(ReturnPointSummary));
        OnPropertyChanged(nameof(ConfigSummary));
        OnPropertyChanged(nameof(IsConfigComplete));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
        RefreshAiSkillPreview();
    }

    private void RefreshAiSkillPreview()
    {
        if (!IsAiSkillMode)
        {
            AiSkillInventoryStatus = "当前为自设按键模式。";
            AiSkillSlots.Clear();
            OnPropertyChanged(nameof(AiSkillInventoryStatus));
            return;
        }

        var window = _binding?.Current;
        if (window is null || _perceptionHub is null || _latestFrames is null)
        {
            AiSkillInventoryStatus = "等待绑定窗口。";
            AiSkillSlots.Clear();
            OnPropertyChanged(nameof(AiSkillInventoryStatus));
            return;
        }

        if (!_perceptionHub.TryGetLatest(window.Id, out var snapshot))
        {
            AiSkillInventoryStatus = "等待运行时读取技能槽。";
            AiSkillSlots.Clear();
            OnPropertyChanged(nameof(AiSkillInventoryStatus));
            return;
        }

        var frame = _latestFrames.GetCached(window.Hwnd, snapshot.FrameSequence);
        var recognized = snapshot.SkillBar.VisualSlots
            .Where(slot => slot.FrameSequence == snapshot.FrameSequence)
            .Where(slot => slot.Identity == VisionSkillIdentityState.Matched)
            .Where(slot => string.Equals(slot.IdentitySource, "icons_ingame", StringComparison.OrdinalIgnoreCase))
            .Where(slot => slot.HangUsable)
            .Where(slot => !string.IsNullOrWhiteSpace(slot.SkillId))
            .OrderBy(slot => slot.Surface)
            .ThenBy(slot => slot.Row)
            .ThenBy(slot => slot.Column)
            .ToArray();

        AiSkillSlots.Clear();
        foreach (var slot in recognized)
        {
            var action = ResolveAiSlotAction(window, slot);
            AiSkillSlots.Add(new AiSkillSlotViewModel(
                CaptureImaging.Crop(frame, slot.SlotBox),
                string.IsNullOrWhiteSpace(slot.SkillName) ? slot.SkillId : slot.SkillName,
                action,
                slot.Cooldown.ToString(),
                slot.MatchScore.ToString("0.000"),
                string.IsNullOrWhiteSpace(slot.SlotId) ? $"{slot.Surface}:{slot.Row},{slot.Column}" : slot.SlotId));
        }

        AiSkillInventoryStatus = recognized.Length == 0
            ? $"本帧未识别到可用于挂机的技能；检测槽位 {snapshot.SkillBar.VisualSlots.Count}。"
            : $"AI 已识别 {recognized.Length} 个技能，检测槽位 {snapshot.SkillBar.VisualSlots.Count}。";
        OnPropertyChanged(nameof(AiSkillInventoryStatus));
    }

    private string ResolveAiSlotAction(BoundGameWindow window, VisionSkillSlotPerception slot)
    {
        if (string.Equals(slot.ActivationKind, "mouse", StringComparison.OrdinalIgnoreCase))
            return slot.ClickX.HasValue && slot.ClickY.HasValue ? $"点击 {slot.ClickX},{slot.ClickY}" : "点击点未知";

        if (slot.Column >= 0
            && _gameHotkeys is not null
            && _gameHotkeys.TryResolve(window.Hwnd, GameInputSemanticIds.SkillSlot(slot.Column + 1), out var binding))
        {
            return binding.Hotkey;
        }

        return "键位未映射";
    }

    private static bool IsActiveState(RunState state) => state is
        RunState.Running or
        RunState.Paused or
        RunState.Recovering or
        RunState.DeathReturn or
        RunState.Reconnecting or
        RunState.Switching;
}
