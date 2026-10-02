using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual string Subtitle => string.Empty;
}

public sealed class AgentFeaturePageViewModel : PageViewModel
{
    public AgentFeaturePageViewModel(
        string title,
        string subtitle,
        string status,
        string nextStep,
        params string[] capabilities)
    {
        Title = title;
        Subtitle = subtitle;
        Status = status;
        NextStep = nextStep;
        Capabilities = capabilities;
    }

    public override string Title { get; }
    public override string Subtitle { get; }
    public string Status { get; }
    public string NextStep { get; }
    public IReadOnlyList<string> Capabilities { get; }
}

public sealed record ShoutChannelOption(ShoutChannel Value, string Display)
{
    public override string ToString() => Display;
}

public sealed class ShoutMessageItemViewModel : ObservableObject
{
    private readonly ShoutMessage _model;
    private readonly Action _onChanged;

    public ShoutMessageItemViewModel(
        ShoutMessage model,
        IReadOnlyList<ShoutChannelOption> channelOptions,
        Action onChanged)
    {
        _model = model;
        ChannelOptions = channelOptions;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public int Number => _model.Order;
    public string Title => $"喊话 {Number}";
    public IReadOnlyList<ShoutChannelOption> ChannelOptions { get; }
    public bool Enabled
    {
        get => _model.Enabled;
        set
        {
            if (_model.Enabled == value) return;
            _model.Enabled = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public string Text
    {
        get => _model.Text;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_model.Text, value, StringComparison.Ordinal)) return;
            _model.Text = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public ShoutChannel Channel
    {
        get => _model.Channel;
        set
        {
            if (_model.Channel == value) return;
            _model.Channel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWorldChannel));
            _onChanged();
        }
    }

    public bool IsWorldChannel => _model.Channel == ShoutChannel.World;
    internal ShoutMessage Model => _model;

    internal void SetOrder(int order)
    {
        if (_model.Order == order) return;
        _model.Order = order;
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(Title));
    }
}

public sealed partial class ShoutPageViewModel : PageViewModel
{
    private readonly IShoutConfigService _config;
    private readonly IShoutController _controller;
    private ShoutConfig? _boundConfig;

    public ShoutPageViewModel(IShoutConfigService config, IShoutController controller)
    {
        _config = config;
        _controller = controller;
        RebuildMessages();
        _config.Changed += (_, _) => UiThread.Dispatch(SyncFromConfig);
    }

    public override string Title => "喊话";
    public override string Subtitle => string.Empty;
    public ObservableCollection<ShoutMessageItemViewModel> Messages { get; } = new();
    public IReadOnlyList<ShoutChannelOption> Channels { get; } = new[]
    {
        new ShoutChannelOption(ShoutChannel.Nearby, "附近频道"),
        new ShoutChannelOption(ShoutChannel.World, "世界频道"),
        new ShoutChannelOption(ShoutChannel.SameCity, "同城频道"),
        new ShoutChannelOption(ShoutChannel.Guild, "帮派频道"),
        new ShoutChannelOption(ShoutChannel.Alliance, "同盟频道"),
        new ShoutChannelOption(ShoutChannel.School, "门派频道"),
        new ShoutChannelOption(ShoutChannel.Team, "队伍频道")
    };

    public bool QueueEnabled
    {
        get => _config.Current.Enabled;
        set
        {
            if (value && !IsChannelRoutingReady) return;
            if (_config.Current.Enabled == value) return;
            _config.Current.Enabled = value;
            OnPropertyChanged();
            _config.NotifyChanged();
        }
    }

    public bool IsChannelRoutingReady => _controller.IsChannelRoutingReady;
    public string ChannelRoutingStatus => _controller.ChannelRoutingStatus;

    [RelayCommand]
    private void AddMessage()
    {
        var model = new ShoutMessage
        {
            Order = Messages.Count + 1,
            Enabled = true,
            Channel = ShoutChannel.Nearby,
            Text = string.Empty
        };
        _config.Current.Messages.Add(model);
        Messages.Add(CreateItem(model));
        Reindex();
        _config.NotifyChanged();
    }

    [RelayCommand]
    private void RemoveMessage(ShoutMessageItemViewModel? item)
    {
        if (item is null) return;
        var index = Messages.IndexOf(item);
        if (index < 0) return;

        Messages.RemoveAt(index);
        _config.Current.Messages.Remove(item.Model);
        Reindex();
        if (Messages.Count == 0)
        {
            _config.Current.Enabled = false;
            OnPropertyChanged(nameof(QueueEnabled));
        }
        _config.NotifyChanged();
    }

    public void MoveMessage(ShoutMessageItemViewModel source, ShoutMessageItemViewModel? target)
    {
        var oldIndex = Messages.IndexOf(source);
        var newIndex = target is null ? Messages.Count - 1 : Messages.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex) return;

        Messages.Move(oldIndex, newIndex);
        _config.Current.Messages.RemoveAt(oldIndex);
        _config.Current.Messages.Insert(newIndex, source.Model);
        Reindex();
        _config.NotifyChanged();
    }

    private void SyncFromConfig()
    {
        OnPropertyChanged(nameof(QueueEnabled));
        OnPropertyChanged(nameof(IsChannelRoutingReady));
        OnPropertyChanged(nameof(ChannelRoutingStatus));

        var models = _config.Current.Messages;
        var requiresRebuild = !ReferenceEquals(_boundConfig, _config.Current)
                              || Messages.Count != models.Count
                              || Messages.Where((item, index) => !ReferenceEquals(item.Model, models[index])).Any();
        if (requiresRebuild)
            RebuildMessages();
    }

    private void RebuildMessages()
    {
        _boundConfig = _config.Current;
        Messages.Clear();
        foreach (var model in _config.Current.Messages)
            Messages.Add(CreateItem(model));
        Reindex();
    }

    private ShoutMessageItemViewModel CreateItem(ShoutMessage model) =>
        new(model, Channels, () => _config.NotifyChanged());

    private void Reindex()
    {
        for (var index = 0; index < Messages.Count; index++)
            Messages[index].SetOrder(index + 1);
    }
}

public sealed record SchoolOption(School Value, string Display)
{
    public override string ToString() => Display;
}

#if LEGACY_FRONTEND
public sealed partial class SkillPresetViewModel : ObservableObject
{
    private readonly SkillPreset _model;
    private readonly Action _onChanged;
    private readonly Action<SkillPresetViewModel>? _captureCooldownTemplate;

    public SkillPresetViewModel(
        SkillPreset model,
        Action onChanged,
        Action<SkillPresetViewModel>? captureCooldownTemplate = null)
    {
        _model = model;
        _onChanged = onChanged;
        _captureCooldownTemplate = captureCooldownTemplate;
    }

    public string Name { get => _model.Name; set { _model.Name = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey
    {
        get => _model.Hotkey;
        set
        {
            _model.Hotkey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CooldownSlotHint));
            _onChanged();
        }
    }
    public int IntervalMs { get => _model.IntervalMs; set { _model.IntervalMs = value; OnPropertyChanged(); _onChanged(); } }
    public double IntervalSeconds
    {
        get => Math.Round(_model.IntervalMs / 1000.0, 2);
        set
        {
            _model.IntervalMs = Math.Max(0, (int)Math.Round(value * 1000));
            OnPropertyChanged();
            OnPropertyChanged(nameof(IntervalMs));
            _onChanged();
        }
    }
    public bool Enabled { get => _model.Enabled; set { _model.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public SkillKind Kind
    {
        get => _model.Kind;
        set
        {
            _model.Kind = value;
            if (value == SkillKind.Buff)
                _model.Condition = SkillCondition.Always;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Condition));
            OnPropertyChanged(nameof(IsBuffSkill));
            _onChanged();
        }
    }

    public SkillCondition Condition
    {
        get => _model.Condition;
        set
        {
            _model.Condition = _model.Kind == SkillKind.Buff ? SkillCondition.Always : value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public bool IsBuffSkill => _model.Kind == SkillKind.Buff;
    public bool CooldownEnabled { get => _model.Cooldown.Enabled; set { _model.Cooldown.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public string CooldownTemplateStatus => string.IsNullOrWhiteSpace(_model.Cooldown.ReadyTemplateHash) ? "\u672a\u91c7\u6837" : "\u5df2\u91c7\u6837";
    public string CooldownSlotHint => IsMappedCooldownHotkey(_model.Hotkey)
        ? $"\u81ea\u52a8\u69fd\u4f4d\uff1a{NormalizeHotkey(_model.Hotkey)}"
        : "\u672a\u5339\u914d\u69fd\u4f4d";

    public void RefreshCooldownStatus()
    {
        OnPropertyChanged(nameof(CooldownEnabled));
        OnPropertyChanged(nameof(CooldownTemplateStatus));
        OnPropertyChanged(nameof(CooldownSlotHint));
    }

    [RelayCommand]
    private void CaptureCooldownTemplate() => _captureCooldownTemplate?.Invoke(this);

    public SkillPreset Model => _model;

    private static bool IsMappedCooldownHotkey(string hotkey)
    {
        return NormalizeHotkey(hotkey) is
            "Q" or "W" or "E" or "R" or "A" or "S" or "D" or "Z" or "X" or "C"
            or "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" or "0"
            or "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or "F8" or "F9" or "F10";
    }

    private static string NormalizeHotkey(string hotkey)
    {
        var value = (hotkey ?? string.Empty).Trim().ToUpperInvariant();
        return value.StartsWith("KEY", StringComparison.Ordinal) ? value[3..] : value;
    }
}

public sealed partial class AutoLoginStepViewModel : ObservableObject
{
    private readonly AutoLoginStep _model;
    private readonly Action _onChanged;

    public AutoLoginStepViewModel(AutoLoginStep model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public string Id => _model.Id;
    public AutoLoginStepKind Kind { get => _model.Kind; set { _model.Kind = value; OnPropertyChanged(); _onChanged(); } }
    public bool Enabled { get => _model.Enabled; set { _model.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public string Hotkey { get => _model.Hotkey; set { _model.Hotkey = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickX { get => _model.ClickX; set { _model.ClickX = value; OnPropertyChanged(); _onChanged(); } }
    public int ClickY { get => _model.ClickY; set { _model.ClickY = value; OnPropertyChanged(); _onChanged(); } }
    public int DelayAfterMs { get => _model.DelayAfterMs; set { _model.DelayAfterMs = value; OnPropertyChanged(); _onChanged(); } }
    public AutoLoginStep Model => _model;
}

internal sealed partial class LegacyGlobalConfigPageViewModel : PageViewModel
{
    private readonly IGlobalConfigService _global;
    private readonly IShoutConfigService _shout;
    private readonly IAutoReconnectConfigService _autoReconnect;
    private readonly IAutoLoginConfigService _autoLogin;
    private readonly IWindowBindingService _binding;
    private readonly IInputService _input;
    private readonly ILogService _log;
    private readonly IAppProfileService _profiles;
    private readonly ISkillCooldownRecognitionService _skillCooldown;
    private CancellationTokenSource? _sliderDraftCts;
    private bool _sliderDraftDirty;
    private GlobalConfig? _skillsBoundConfig;
    private double _hpThresholdDraftPct;
    private double _mpThresholdDraftPct;
    private double _petHpThresholdDraftPct;
    private double _petFoodThresholdDraftPct;
    private double _bloodManaThresholdDraftPct;
    private double _symbiosisHealThresholdDraftPct;
    private double _emHealThresholdDraftPct;
    private double _acquireIntervalDraftSeconds;

    public LegacyGlobalConfigPageViewModel(
        IGlobalConfigService global,
        IShoutConfigService shout,
        IAutoReconnectConfigService autoReconnect,
        IAutoLoginConfigService autoLogin,
        IWindowBindingService binding,
        IInputService input,
        ILogService log,
        IAppProfileService profiles,
        ISkillCooldownRecognitionService skillCooldown)
    {
        _global = global;
        _shout = shout;
        _autoReconnect = autoReconnect;
        _autoLogin = autoLogin;
        _binding = binding;
        _input = input;
        _log = log;
        _profiles = profiles;
        _skillCooldown = skillCooldown;
        RebuildSkills();
        RebuildAutoLoginSteps();
        _global.Changed += (_, _) => UiThread.Dispatch(OnGlobalChanged);
        _shout.Changed += (_, _) => UiThread.Dispatch(OnShoutChanged);
        _autoReconnect.Changed += (_, _) => UiThread.Dispatch(OnAutoReconnectChanged);
        _autoLogin.Changed += (_, _) => UiThread.Dispatch(OnAutoLoginChanged);
        _selectedSchool = Schools.First(s => s.Value == _global.Current.School);
        SyncSliderDraftsFromConfig();
    }

    public override string Title => "通用配置";
    public override string Subtitle => "全局设置。";

    private GlobalConfig C => _global.Current;

    public IReadOnlyList<SchoolOption> Schools { get; } = new List<SchoolOption>
    {
        new(School.None, "未选择"),
        new(School.Shaolin, "少林"),
        new(School.Wudang, "武当"),
        new(School.Emei, "峨眉"),
        new(School.Gaibang, "丐帮"),
        new(School.Xiaoyao, "逍遥"),
        new(School.Tianshan, "天山"),
        new(School.Mingjiao, "明教"),
        new(School.Xingxiu, "星宿"),
        new(School.Tangmen, "唐门"),
        new(School.Murong, "慕容"),
    };

    public Array Conditions { get; } = Enum.GetValues(typeof(SkillCondition));
    public Array SkillKinds { get; } = Enum.GetValues(typeof(SkillKind));
    public Array TargetFilters { get; } = Enum.GetValues(typeof(TargetFilter));
    public Array InputDispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public Array GameBuiltInHangSwitchModes { get; } = Enum.GetValues(typeof(GameBuiltInHangSwitchMode));
    public IReadOnlyList<CombatBackendPreference> CombatBackendPreferences { get; } =
        new[]
        {
            CombatBackendPreference.Auto,
            CombatBackendPreference.InternalPlugin,
            CombatBackendPreference.ExternalOcr
        };
    public Array AutoLoginStepKinds { get; } = Enum.GetValues(typeof(AutoLoginStepKind));

    [ObservableProperty] private SchoolOption _selectedSchool;
    [ObservableProperty] private bool _isSkillEditorOpen;

    partial void OnSelectedSchoolChanged(SchoolOption value) => _global.SetSchool(value.Value);
    partial void OnIsSkillEditorOpenChanged(bool value) => OnPropertyChanged(nameof(SkillEditorButtonText));

    public string SkillEditorButtonText => IsSkillEditorOpen ? "收起" : "管理";
    public string SkillSummary => $"已启用 {C.Skills.Count(skill => skill.Enabled)} / {C.Skills.Count}";

    [RelayCommand]
    private void ToggleSkillEditor() => IsSkillEditorOpen = !IsSkillEditorOpen;

    private void OnGlobalChanged()
    {
        var current = Schools.First(s => s.Value == C.School);
        if (!Equals(SelectedSchool, current))
            SelectedSchool = current;

        OnPropertyChanged(nameof(EnableEmHeal));
        OnPropertyChanged(nameof(CanEditEmHeal));
        OnPropertyChanged(nameof(EmHealHint));
        OnPropertyChanged(nameof(CombatBackendPreference));
        OnPropertyChanged(nameof(InputDispatchMode));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchMode));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchHotkey));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchClickX));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchClickY));
        OnPropertyChanged(nameof(GameBuiltInHangSwitchCooldownSeconds));
        OnPropertyChanged(nameof(YieldToManualInput));
        OnPropertyChanged(nameof(ManualInputYieldSeconds));
        OnPropertyChanged(nameof(AttackHotkey));
        OnPropertyChanged(nameof(AttackIntervalSeconds));
        OnPropertyChanged(nameof(AcquireHotkey));
        OnPropertyChanged(nameof(PetDeployHotkey));
        OnPropertyChanged(nameof(PetDeployUseClick));
        OnPropertyChanged(nameof(PetDeployClickX));
        OnPropertyChanged(nameof(PetDeployClickBottomOffset));
        OnPropertyChanged(nameof(TownPortalHotkey));
        OnPropertyChanged(nameof(EarthSpiritHotkey));
        OnPropertyChanged(nameof(HealHotkey));
        OnPropertyChanged(nameof(ManaHotkey));
        OnPropertyChanged(nameof(BloodManaHotkey));
        OnPropertyChanged(nameof(SymbiosisHealHotkey));
        OnPropertyChanged(nameof(BloodManaUseClick));
        OnPropertyChanged(nameof(BloodManaClickX));
        OnPropertyChanged(nameof(BloodManaClickBottomOffset));
        OnPropertyChanged(nameof(SymbiosisHealUseClick));
        OnPropertyChanged(nameof(SymbiosisHealClickX));
        OnPropertyChanged(nameof(SymbiosisHealClickBottomOffset));
        OnPropertyChanged(nameof(PetManaHotkey));
        OnPropertyChanged(nameof(PetHealHotkey));
        OnPropertyChanged(nameof(PetFoodHotkey));
        OnPropertyChanged(nameof(SkillSummary));
        if (!ReferenceEquals(_skillsBoundConfig, C))
            RebuildSkills();
        if (!_sliderDraftDirty)
            SyncSliderDraftsFromConfig();
    }

    private void OnShoutChanged()
    {
        OnPropertyChanged(nameof(ShoutEnabled));
        OnPropertyChanged(nameof(ShoutOpenChatHotkey));
        OnPropertyChanged(nameof(ShoutCommitHotkey));
        OnPropertyChanged(nameof(ShoutIntervalMs));
        OnPropertyChanged(nameof(ShoutMessageDelayMs));
        OnPropertyChanged(nameof(ShoutCommitDelayMs));
        OnPropertyChanged(nameof(ShoutRotateMessages));
        OnPropertyChanged(nameof(ShoutMessagesText));
    }

    private void OnAutoReconnectChanged()
    {
        OnPropertyChanged(nameof(AutoReconnectEnabled));
        OnPropertyChanged(nameof(AutoReconnectCheckIntervalMs));
        OnPropertyChanged(nameof(AutoReconnectRebindTimeoutMs));
        OnPropertyChanged(nameof(AutoReconnectRelaunchDelayMs));
        OnPropertyChanged(nameof(AutoReconnectMaxRestartAttempts));
        OnPropertyChanged(nameof(AutoReconnectRestartWhenProcessExited));
        OnPropertyChanged(nameof(AutoReconnectProcessPathOverride));
    }

    private void OnAutoLoginChanged()
    {
        OnPropertyChanged(nameof(AutoLoginEnabled));
        OnPropertyChanged(nameof(AutoLoginStartDelayMs));
        OnPropertyChanged(nameof(AutoLoginStopAfterSequence));
        RebuildAutoLoginSteps();
    }

    public int HpThresholdPct { get => C.HpThresholdPct; set { C.HpThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int MpThresholdPct { get => C.MpThresholdPct; set { C.MpThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int PetHpThresholdPct { get => C.PetHpThresholdPct; set { C.PetHpThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int PetFoodThresholdPct { get => C.PetFoodThresholdPct; set { C.PetFoodThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int BloodManaThresholdPct { get => C.BloodManaThresholdPct; set { C.BloodManaThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int SymbiosisHealThresholdPct { get => C.SymbiosisHealThresholdPct; set { C.SymbiosisHealThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public double HpThresholdDraftPct { get => _hpThresholdDraftPct; set => SetPercentSliderDraft(ref _hpThresholdDraftPct, value, nameof(HpThresholdDraftPct)); }
    public double MpThresholdDraftPct { get => _mpThresholdDraftPct; set => SetPercentSliderDraft(ref _mpThresholdDraftPct, value, nameof(MpThresholdDraftPct)); }
    public double PetHpThresholdDraftPct { get => _petHpThresholdDraftPct; set => SetPercentSliderDraft(ref _petHpThresholdDraftPct, value, nameof(PetHpThresholdDraftPct)); }
    public double PetFoodThresholdDraftPct { get => _petFoodThresholdDraftPct; set => SetPercentSliderDraft(ref _petFoodThresholdDraftPct, value, nameof(PetFoodThresholdDraftPct)); }
    public double BloodManaThresholdDraftPct { get => _bloodManaThresholdDraftPct; set => SetPercentSliderDraft(ref _bloodManaThresholdDraftPct, value, nameof(BloodManaThresholdDraftPct)); }
    public double SymbiosisHealThresholdDraftPct { get => _symbiosisHealThresholdDraftPct; set => SetPercentSliderDraft(ref _symbiosisHealThresholdDraftPct, value, nameof(SymbiosisHealThresholdDraftPct)); }
    public int PublicCooldownMs { get => C.PublicCooldownMs; set { C.PublicCooldownMs = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public InputDispatchMode InputDispatchMode { get => C.InputDispatchMode; set { C.InputDispatchMode = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool YieldToManualInput { get => C.YieldToManualInput; set { C.YieldToManualInput = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public double ManualInputYieldSeconds
    {
        get => Math.Round(C.ManualInputYieldMs / 1000.0, 1);
        set
        {
            C.ManualInputYieldMs = Math.Clamp((int)Math.Round(value * 1000), 300, 5000);
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }
    public CombatBackendPreference CombatBackendPreference { get => C.CombatBackendPreference; set { C.CombatBackendPreference = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public GameBuiltInHangSwitchMode GameBuiltInHangSwitchMode { get => C.GameBuiltInHangSwitchMode; set { C.GameBuiltInHangSwitchMode = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string GameBuiltInHangSwitchHotkey { get => C.GameBuiltInHangSwitchHotkey; set { C.GameBuiltInHangSwitchHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int GameBuiltInHangSwitchClickX { get => C.GameBuiltInHangSwitchClickX; set { C.GameBuiltInHangSwitchClickX = Math.Max(0, value); OnPropertyChanged(); _global.NotifyChanged(); } }
    public int GameBuiltInHangSwitchClickY { get => C.GameBuiltInHangSwitchClickY; set { C.GameBuiltInHangSwitchClickY = Math.Max(0, value); OnPropertyChanged(); _global.NotifyChanged(); } }
    public double GameBuiltInHangSwitchCooldownSeconds
    {
        get => Math.Round(C.GameBuiltInHangSwitchCooldownMs / 1000.0, 1);
        set
        {
            C.GameBuiltInHangSwitchCooldownMs = Math.Clamp((int)Math.Round(value * 1000), 500, 60000);
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }

    public string AttackHotkey { get => C.AttackHotkey; set { C.AttackHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int AttackIntervalMs { get => C.AttackIntervalMs; set { C.AttackIntervalMs = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public double AttackIntervalSeconds
    {
        get => Math.Round(C.AttackIntervalMs / 1000.0, 2);
        set
        {
            C.AttackIntervalMs = Math.Max(0, (int)Math.Round(value * 1000));
            OnPropertyChanged();
            OnPropertyChanged(nameof(AttackIntervalMs));
            _global.NotifyChanged();
        }
    }
    public string MountHotkey { get => C.MountHotkey; set { C.MountHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string MeditateHotkey { get => C.MeditateHotkey; set { C.MeditateHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string PetDeployHotkey { get => C.PetDeployHotkey; set { C.PetDeployHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool PetDeployUseClick
    {
        get => C.PetDeployActivationMode == PetSkillActivationMode.ClientClick;
        set
        {
            C.PetDeployActivationMode = value ? PetSkillActivationMode.ClientClick : PetSkillActivationMode.Hotkey;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }
    public int PetDeployClickX { get => C.PetDeployClickX; set { C.PetDeployClickX = Math.Max(0, value); OnPropertyChanged(); _global.NotifyChanged(); } }
    public int PetDeployClickBottomOffset { get => C.PetDeployClickBottomOffset; set { C.PetDeployClickBottomOffset = Math.Max(0, value); OnPropertyChanged(); _global.NotifyChanged(); } }
    public string HealHotkey { get => C.HealHotkey; set { C.HealHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string ManaHotkey { get => C.ManaHotkey; set { C.ManaHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string PetManaHotkey { get => C.PetManaHotkey; set { C.PetManaHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string PetHealHotkey { get => C.PetHealHotkey; set { C.PetHealHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string PetFoodHotkey { get => C.PetFoodHotkey; set { C.PetFoodHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string BloodManaHotkey { get => C.BloodManaHotkey; set { C.BloodManaHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string SymbiosisHealHotkey { get => C.SymbiosisHealHotkey; set { C.SymbiosisHealHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool BloodManaUseClick
    {
        get => C.BloodManaActivationMode == PetSkillActivationMode.ClientClick;
        set
        {
            C.BloodManaActivationMode = value ? PetSkillActivationMode.ClientClick : PetSkillActivationMode.Hotkey;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }
    public int BloodManaClickX { get => C.BloodManaClickX; set { C.BloodManaClickX = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int BloodManaClickBottomOffset { get => C.BloodManaClickBottomOffset; set { C.BloodManaClickBottomOffset = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool SymbiosisHealUseClick
    {
        get => C.SymbiosisHealActivationMode == PetSkillActivationMode.ClientClick;
        set
        {
            C.SymbiosisHealActivationMode = value ? PetSkillActivationMode.ClientClick : PetSkillActivationMode.Hotkey;
            OnPropertyChanged();
            _global.NotifyChanged();
        }
    }
    public int SymbiosisHealClickX { get => C.SymbiosisHealClickX; set { C.SymbiosisHealClickX = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int SymbiosisHealClickBottomOffset { get => C.SymbiosisHealClickBottomOffset; set { C.SymbiosisHealClickBottomOffset = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string TownPortalHotkey { get => C.TownPortalHotkey; set { C.TownPortalHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string EarthSpiritHotkey { get => C.EarthSpiritHotkey; set { C.EarthSpiritHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }

    public bool ShoutEnabled { get => _shout.Current.Enabled; set { _shout.Current.Enabled = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public string ShoutOpenChatHotkey { get => _shout.Current.OpenChatHotkey; set { _shout.Current.OpenChatHotkey = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public string ShoutCommitHotkey { get => _shout.Current.CommitHotkey; set { _shout.Current.CommitHotkey = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public int ShoutIntervalMs { get => _shout.Current.IntervalMs; set { _shout.Current.IntervalMs = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public int ShoutMessageDelayMs { get => _shout.Current.MessageDelayMs; set { _shout.Current.MessageDelayMs = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public int ShoutCommitDelayMs { get => _shout.Current.CommitDelayMs; set { _shout.Current.CommitDelayMs = value; OnPropertyChanged(); _shout.NotifyChanged(); } }
    public bool ShoutRotateMessages { get => _shout.Current.RotateMessages; set { _shout.Current.RotateMessages = value; OnPropertyChanged(); _shout.NotifyChanged(); } }

    public bool AutoReconnectEnabled { get => _autoReconnect.Current.Enabled; set { _autoReconnect.Current.Enabled = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public int AutoReconnectCheckIntervalMs { get => _autoReconnect.Current.CheckIntervalMs; set { _autoReconnect.Current.CheckIntervalMs = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public int AutoReconnectRebindTimeoutMs { get => _autoReconnect.Current.RebindTimeoutMs; set { _autoReconnect.Current.RebindTimeoutMs = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public int AutoReconnectRelaunchDelayMs { get => _autoReconnect.Current.RelaunchDelayMs; set { _autoReconnect.Current.RelaunchDelayMs = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public int AutoReconnectMaxRestartAttempts { get => _autoReconnect.Current.MaxRestartAttempts; set { _autoReconnect.Current.MaxRestartAttempts = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public bool AutoReconnectRestartWhenProcessExited { get => _autoReconnect.Current.RestartWhenProcessExited; set { _autoReconnect.Current.RestartWhenProcessExited = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }
    public string AutoReconnectProcessPathOverride { get => _autoReconnect.Current.ProcessPathOverride; set { _autoReconnect.Current.ProcessPathOverride = value; OnPropertyChanged(); _autoReconnect.NotifyChanged(); } }

    public bool AutoLoginEnabled { get => _autoLogin.Current.Enabled; set { _autoLogin.Current.Enabled = value; OnPropertyChanged(); _autoLogin.NotifyChanged(); } }
    public int AutoLoginStartDelayMs { get => _autoLogin.Current.StartDelayMs; set { _autoLogin.Current.StartDelayMs = value; OnPropertyChanged(); _autoLogin.NotifyChanged(); } }
    public bool AutoLoginStopAfterSequence { get => _autoLogin.Current.StopAfterSequence; set { _autoLogin.Current.StopAfterSequence = value; OnPropertyChanged(); _autoLogin.NotifyChanged(); } }

    public string ShoutMessagesText
    {
        get => string.Join(Environment.NewLine, _shout.Current.Messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text));
        set
        {
            _shout.Current.Messages.Clear();
            foreach (var line in (value ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _shout.Current.Messages.Add(new ShoutMessage
                {
                    Enabled = true,
                    Text = line.Trim(),
                    SourceModule = "内置",
                    SourcePath = string.Empty
                });
            }

            OnPropertyChanged();
            _shout.NotifyChanged();
        }
    }

    public bool EnableEmHeal { get => C.EnableEmHeal; set { C.EnableEmHeal = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool CanEditEmHeal => _global.SchoolProvidesHeal(C.School);
    public string EmHealHotkey { get => C.EmHealHotkey; set { C.EmHealHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int EmHealThresholdPct { get => C.EmHealThresholdPct; set { C.EmHealThresholdPct = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public double EmHealThresholdDraftPct { get => _emHealThresholdDraftPct; set => SetPercentSliderDraft(ref _emHealThresholdDraftPct, value, nameof(EmHealThresholdDraftPct)); }
    public string EmHealHint => CanEditEmHeal ? "已启用。" : "选择峨眉后可用。";

    public TargetFilter TargetFilter { get => C.Targeting.Filter; set { C.Targeting.Filter = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSpecificName)); _global.NotifyChanged(); } }
    public bool IsSpecificName => C.Targeting.Filter == TargetFilter.SpecificName;
    public string SpecificName { get => C.Targeting.SpecificName; set { C.Targeting.SpecificName = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public string AcquireHotkey { get => C.Targeting.AcquireHotkey; set { C.Targeting.AcquireHotkey = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int ScanIntervalMs { get => C.Targeting.ScanIntervalMs; set { C.Targeting.ScanIntervalMs = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool OnlyAttackMarked { get => C.Targeting.OnlyAttackMarked; set { C.Targeting.OnlyAttackMarked = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public bool AutoAcquireWhenNoTarget { get => C.Targeting.AutoAcquireWhenNoTarget; set { C.Targeting.AutoAcquireWhenNoTarget = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public int AcquireIntervalMs { get => C.Targeting.AcquireIntervalMs; set { C.Targeting.AcquireIntervalMs = value; OnPropertyChanged(); _global.NotifyChanged(); } }
    public double AcquireIntervalSeconds
    {
        get => Math.Round(C.Targeting.AcquireIntervalMs / 1000.0, 1);
        set
        {
            C.Targeting.AcquireIntervalMs = Math.Max(200, (int)Math.Round(value * 1000));
            OnPropertyChanged();
            OnPropertyChanged(nameof(AcquireIntervalMs));
            _global.NotifyChanged();
        }
    }
    public double AcquireIntervalDraftSeconds
    {
        get => _acquireIntervalDraftSeconds;
        set
        {
            var next = Math.Clamp(value, 0.2, 3.0);
            if (Math.Abs(_acquireIntervalDraftSeconds - next) < 0.001)
                return;

            _acquireIntervalDraftSeconds = next;
            OnPropertyChanged();
            _sliderDraftDirty = true;
            QueueSliderDraftCommit();
        }
    }
    public int TargetLostDelayMs { get => C.Targeting.TargetLostDelayMs; set { C.Targeting.TargetLostDelayMs = value; OnPropertyChanged(); _global.NotifyChanged(); } }

    public ObservableCollection<SkillPresetViewModel> Skills { get; } = new();
    public ObservableCollection<AutoLoginStepViewModel> AutoLoginSteps { get; } = new();

    private void SyncSliderDraftsFromConfig()
    {
        _hpThresholdDraftPct = C.HpThresholdPct;
        _mpThresholdDraftPct = C.MpThresholdPct;
        _petHpThresholdDraftPct = C.PetHpThresholdPct;
        _petFoodThresholdDraftPct = C.PetFoodThresholdPct;
        _bloodManaThresholdDraftPct = C.BloodManaThresholdPct;
        _symbiosisHealThresholdDraftPct = C.SymbiosisHealThresholdPct;
        _emHealThresholdDraftPct = C.EmHealThresholdPct;
        _acquireIntervalDraftSeconds = Math.Round(C.Targeting.AcquireIntervalMs / 1000.0, 1);

        OnPropertyChanged(nameof(HpThresholdDraftPct));
        OnPropertyChanged(nameof(MpThresholdDraftPct));
        OnPropertyChanged(nameof(PetHpThresholdDraftPct));
        OnPropertyChanged(nameof(PetFoodThresholdDraftPct));
        OnPropertyChanged(nameof(BloodManaThresholdDraftPct));
        OnPropertyChanged(nameof(SymbiosisHealThresholdDraftPct));
        OnPropertyChanged(nameof(EmHealThresholdDraftPct));
        OnPropertyChanged(nameof(AcquireIntervalDraftSeconds));
    }

    private void SetPercentSliderDraft(ref double field, double value, string propertyName)
    {
        var next = Math.Clamp(value, 1, 100);
        if (Math.Abs(field - next) < 0.01)
            return;

        field = next;
        OnPropertyChanged(propertyName);
        _sliderDraftDirty = true;
        QueueSliderDraftCommit();
    }

    private void QueueSliderDraftCommit()
    {
        _sliderDraftCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sliderDraftCts = cts;
        _ = CommitSliderDraftsAsync(cts.Token);
    }

    private async Task CommitSliderDraftsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(320, token);
            if (token.IsCancellationRequested)
                return;

            UiThread.Dispatch(() =>
            {
                C.HpThresholdPct = (int)Math.Round(_hpThresholdDraftPct);
                C.MpThresholdPct = (int)Math.Round(_mpThresholdDraftPct);
                C.PetHpThresholdPct = (int)Math.Round(_petHpThresholdDraftPct);
                C.PetFoodThresholdPct = (int)Math.Round(_petFoodThresholdDraftPct);
                C.BloodManaThresholdPct = (int)Math.Round(_bloodManaThresholdDraftPct);
                C.SymbiosisHealThresholdPct = (int)Math.Round(_symbiosisHealThresholdDraftPct);
                C.EmHealThresholdPct = (int)Math.Round(_emHealThresholdDraftPct);
                C.Targeting.AcquireIntervalMs = Math.Max(200, (int)Math.Round(_acquireIntervalDraftSeconds * 1000));
                _sliderDraftDirty = false;
                _global.NotifyChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RebuildSkills()
    {
        _skillsBoundConfig = C;
        Skills.Clear();
        foreach (var s in C.Skills)
            Skills.Add(new SkillPresetViewModel(s, _global.NotifyChanged, CaptureSkillCooldownTemplate));
        OnPropertyChanged(nameof(SkillSummary));
    }

    private void RebuildAutoLoginSteps()
    {
        AutoLoginSteps.Clear();
        foreach (var s in _autoLogin.Current.Steps)
            AutoLoginSteps.Add(new AutoLoginStepViewModel(s, _autoLogin.NotifyChanged));
    }

    [RelayCommand]
    private void AddSkill()
    {
        var model = new SkillPreset { Name = $"技能{C.Skills.Count + 1}", Hotkey = "", Priority = C.Skills.Count + 1, Kind = SkillKind.Damage, Condition = SkillCondition.Always };
        C.Skills.Add(model);
        Skills.Add(new SkillPresetViewModel(model, _global.NotifyChanged, CaptureSkillCooldownTemplate));
        OnPropertyChanged(nameof(SkillSummary));
        _global.NotifyChanged();
    }

    [RelayCommand]
    private void RemoveSkill(SkillPresetViewModel? vm)
    {
        if (vm is null) return;
        C.Skills.Remove(vm.Model);
        Skills.Remove(vm);
        OnPropertyChanged(nameof(SkillSummary));
        _global.NotifyChanged();
    }

    private void CaptureSkillCooldownTemplate(SkillPresetViewModel vm)
    {
        var window = _binding.Current;
        if (window is null)
        {
            _log.Warn("\u6280\u80fdCD", "\u672a\u7ed1\u5b9a\u7a97\u53e3\uff0c\u65e0\u6cd5\u91c7\u6837\u6280\u80fd\u69fd\u3002");
            return;
        }

        if (_skillCooldown.TryCaptureReadyTemplate(window, vm.Model, C, out var inspection, out var error))
        {
            vm.RefreshCooldownStatus();
            _global.NotifyChanged();
            _log.Info("\u6280\u80fdCD", $"\u5df2\u91c7\u6837\u53ef\u7528\u72b6\u6001\uff1a{vm.Name} @ ({inspection.X},{inspection.Y},{inspection.Width}x{inspection.Height})\u3002", window.Id);
            return;
        }

        _log.Warn("\u6280\u80fdCD", $"\u91c7\u6837\u5931\u8d25\uff1a{vm.Name} | {error}\u3002", window.Id);
    }

    [RelayCommand]
    private void TestBloodManaClick()
    {
        C.BloodManaActivationMode = PetSkillActivationMode.ClientClick;
        _global.NotifyChanged();
        TestPetSkillClick("\u8840\u8ff9\u56de\u84dd", C.BloodManaClickX, C.BloodManaClickBottomOffset);
    }

    [RelayCommand]
    private void TestSymbiosisHealClick()
    {
        C.SymbiosisHealActivationMode = PetSkillActivationMode.ClientClick;
        _global.NotifyChanged();
        TestPetSkillClick("\u5171\u751f\u56de\u8840", C.SymbiosisHealClickX, C.SymbiosisHealClickBottomOffset);
    }

    [RelayCommand]
    private void TestPetDeployClick()
    {
        C.PetDeployActivationMode = PetSkillActivationMode.ClientClick;
        _global.NotifyChanged();
        TestPetSkillClick("\u5ba0\u7269\u51fa\u6218", C.PetDeployClickX, C.PetDeployClickBottomOffset);
    }

    [RelayCommand]
    private async Task OpenPetDeploySampler()
    {
        var profile = _profiles.CurrentProfileName
                      ?? _profiles.ListProfileNames().FirstOrDefault()
                      ?? "";
        var args = new List<string> { "-RefreshCalibration" };
        if (!string.IsNullOrWhiteSpace(profile))
        {
            args.Add("-Profile");
            args.Add(profile);
        }

        _log.Info("\u5ba0\u7269\u51fa\u6218", "\u6b63\u5728\u751f\u6210\u5ba0\u7269\u51fa\u6218\u6807\u5b9a\u56fe\uff0c\u8be5\u52a8\u4f5c\u4e0d\u70b9\u51fb\u6e38\u620f\u3001\u4e0d\u5199\u914d\u7f6e\u3002");
        var result = await ToolLauncher.RunPowerShellScriptAsync("Run-PetDeployReadiness.ps1", args);
        if (!result.Success)
        {
            _log.Warn("\u5ba0\u7269\u51fa\u6218", $"标定图生成失败：{ShortToolText(result.Error, result.Output)}");
            return;
        }

        var bottomOverlay = ReadToolLineValue(result.Output, "BottomOverlay");
        var overlay = ReadToolLineValue(result.Output, "Overlay");
        var json = ReadToolLineValue(result.Output, "Json");
        var opened = false;
        if (!string.IsNullOrWhiteSpace(bottomOverlay) && ToolLauncher.TryOpenPath(bottomOverlay, out var bottomDetail))
        {
            opened = true;
            _log.Info("\u5ba0\u7269\u51fa\u6218", $"已打开底部候选图：{bottomOverlay}");
        }
        else if (!string.IsNullOrWhiteSpace(overlay) && ToolLauncher.TryOpenPath(overlay, out var overlayDetail))
        {
            opened = true;
            _log.Info("\u5ba0\u7269\u51fa\u6218", $"已打开全图候选图：{overlay}");
        }

        if (!opened)
        {
            _log.Warn("\u5ba0\u7269\u51fa\u6218", $"标定图生成完成，但没有可打开的候选图。{ShortToolText(result.Error, result.Output)}");
            return;
        }

        _log.Info(
            "\u5ba0\u7269\u51fa\u6218",
            $"请在候选图中确认当前宠物出战按钮中心，再按日志中的候选 rank 写入。证据：{json}");
    }

    private static string ReadToolLineValue(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }

        return string.Empty;
    }

    private static string ShortToolText(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        if (string.IsNullOrWhiteSpace(text))
            return "无输出。";

        text = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return text.Length <= 160 ? text : text[..160] + "...";
    }

    private void TestPetSkillClick(string label, int x, int bottomOffset)
    {
        var window = _binding.Current;
        if (window is null)
        {
            _log.Warn("\u5ba0\u7269\u6280\u80fd", "\u8bf7\u5148\u7ed1\u5b9a\u6e38\u620f\u7a97\u53e3\u3002");
            return;
        }

        if (window.ClientWidth <= 0 || window.ClientHeight <= 0)
        {
            _log.Warn("\u5ba0\u7269\u6280\u80fd", "\u7a97\u53e3\u5c3a\u5bf8\u65e0\u6548\uff0c\u65e0\u6cd5\u8ba1\u7b97\u5ba0\u7269\u6280\u80fd\u69fd\u4f4d\u3002", window.Id);
            return;
        }

        if (x <= 0 || bottomOffset <= 0)
        {
            _log.Warn(
                "\u5ba0\u7269\u6280\u80fd",
                $"{label}\u672a\u6807\u5b9a\u70b9\u51fb\u4f4d\uff1aX={x}, \u5e95\u8ddd={bottomOffset}\u3002\u5148\u70b9\u201c\u6807\u5b9a\u201d\u9009\u62e9\u5ba2\u6237\u533a\u4e2d\u7684\u51c6\u786e\u6309\u94ae\u4e2d\u5fc3\uff0c\u518d\u6d4b\u8bd5\u3002",
                window.Id);
            return;
        }

        var y = window.ClientHeight - Math.Max(0, bottomOffset);
        if (x < 0 || x >= window.ClientWidth || y < 0 || y >= window.ClientHeight)
        {
            _log.Warn(
                "\u5ba0\u7269\u6280\u80fd",
                $"{label}\u6d4b\u8bd5\u5750\u6807\u8d85\u51fa\u5ba2\u6237\u533a\uff1aX={x}, Y={y}, \u5ba2\u6237\u533a={window.ClientSizeText}\u3002",
                window.Id);
            return;
        }

        _input.DefaultDispatchMode = C.InputDispatchMode;
        var yield = _input.GetYieldStatus(window.Hwnd, C.InputDispatchMode);
        if (yield.ShouldYield)
        {
            _log.Warn(
                "\u5ba0\u7269\u6280\u80fd",
                $"{label}\u6d4b\u8bd5\u70b9\u51fb\u5df2\u8ba9\u63a7\uff1a{ShortPetSkillYieldReason(yield.Reason)}",
                window.Id);
            return;
        }

        var ok = _input.Click(window.Hwnd, x, y, C.InputDispatchMode);
        if (ok)
        {
            _log.Info(
                "\u5ba0\u7269\u6280\u80fd",
                $"{label}\u6d4b\u8bd5\u70b9\u51fb\uff1aX={x}, Y={y}, \u5e95\u8ddd={bottomOffset}, \u901a\u9053={C.InputDispatchMode}\u3002",
                window.Id);
        }
        else
        {
            _log.Warn(
                "\u5ba0\u7269\u6280\u80fd",
                $"{label}\u6d4b\u8bd5\u70b9\u51fb\u5931\u8d25\uff1aX={x}, Y={y}, \u5e95\u8ddd={bottomOffset}\u3002",
                window.Id);
        }
    }

    private static string ShortPetSkillYieldReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "\u68c0\u6d4b\u5230\u4eba\u5de5\u64cd\u4f5c\uff0c\u672c\u6b21\u81ea\u52a8\u8f93\u5165\u8df3\u8fc7\u3002";

        return reason.Length <= 80 ? reason : reason[..80] + "...";
    }

    [RelayCommand]
    private void AddAutoLoginHotkeyStep()
    {
        _autoLogin.AddStep(new AutoLoginStep
        {
            Kind = AutoLoginStepKind.Hotkey,
            Label = $"热键动作 {_autoLogin.Current.Steps.Count + 1}",
            Hotkey = "Enter",
            DelayAfterMs = 1000
        });
    }

    [RelayCommand]
    private void AddAutoLoginClickStep()
    {
        _autoLogin.AddStep(new AutoLoginStep
        {
            Kind = AutoLoginStepKind.Click,
            Label = $"点击动作 {_autoLogin.Current.Steps.Count + 1}",
            ClickX = 512,
            ClickY = 384,
            DelayAfterMs = 1000
        });
    }

    [RelayCommand]
    private void AddAutoLoginWaitStep()
    {
        _autoLogin.AddStep(new AutoLoginStep
        {
            Kind = AutoLoginStepKind.Wait,
            Label = $"等待 {_autoLogin.Current.Steps.Count + 1}",
            DelayAfterMs = 1500
        });
    }

    [RelayCommand]
    private void RemoveAutoLoginStep(AutoLoginStepViewModel? vm)
    {
        if (vm is null) return;
        _autoLogin.RemoveStep(vm.Id);
    }

    [RelayCommand]
    private void MoveAutoLoginStepUp(AutoLoginStepViewModel? vm)
    {
        if (vm is null) return;
        _autoLogin.MoveStep(vm.Id, -1);
    }

    [RelayCommand]
    private void MoveAutoLoginStepDown(AutoLoginStepViewModel? vm)
    {
        if (vm is null) return;
        _autoLogin.MoveStep(vm.Id, 1);
    }

    [RelayCommand]
    private void ExportConfig()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 TLBB Agent 通用配置",
            FileName = $"TLBB-Agent-通用配置-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "TLBB Agent 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() == true)
            _global.ExportToFile(dialog.FileName);
    }

    [RelayCommand]
    private void ImportConfig()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入配置",
            Filter = "TLBB Agent 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            _global.ImportFromFile(dialog.FileName);
    }
}
#endif

public sealed partial class ModeConfigPageViewModel : PageViewModel
{
    private readonly ModuleDescriptor _descriptor;
    private readonly IAutomationController? _controller;

    public ModeConfigPageViewModel(ModuleDescriptor descriptor, IAutomationController? controller, ILogService log)
    {
        _descriptor = descriptor;
        _controller = controller;
        if (_controller is not null)
        {
            _controller.StateChanged += (_, _) => UiThread.Dispatch(() =>
            {
                OnPropertyChanged(nameof(CurrentStateName));
                OnPropertyChanged(nameof(RunState));
            });
        }
    }

    public override string Title => _descriptor.Title;
    public override string Subtitle => _descriptor.Description;

    public string Group => _descriptor.Group;
    public string SourceModule => _descriptor.SourceModule;
    public string Priority => _descriptor.Priority;
    public ModuleIntegrationStatus Status => _descriptor.Status;

    public bool HasController => _controller is not null;
    public string CurrentStateName => _controller?.CurrentStateName ?? "-";
    public RunState RunState => _controller?.RunState ?? RunState.Idle;

    public string ConfigHint => _descriptor.Group switch
    {
        "挂机" => "技能、目标、坐标与恢复。",
        "活动" => "路线、识别、点击与重试。",
        "生活" => "路线、资源、背包与重试。",
        "任务自动化" => "流程、坐标、次数与重试。",
        _ => "独立配置。"
    };
}

public sealed partial class CombatRegionViewModel : ObservableObject
{
    private readonly OcrRegionConfig _config;
    private readonly Action _onChanged;
    private int _projectionClientWidth;
    private int _projectionClientHeight;
    private bool _rightAnchored;

    public CombatRegionViewModel(string key, string name, OcrRegionConfig config, Action onChanged)
    {
        Key = key;
        Name = name;
        _config = config;
        _onChanged = onChanged;
        RefreshOverlay();
    }

    public string Key { get; }
    public string Name { get; }
    public OcrRegionConfig Config => _config;
    public int OverlayX { get; private set; }
    public int OverlayY { get; private set; }
    public int OverlayWidth { get; private set; }
    public int OverlayHeight { get; private set; }

    public override string ToString() => Name;

    public bool Enabled { get => _config.Enabled; set { _config.Enabled = value; OnPropertyChanged(); RefreshOverlay(); _onChanged(); } }
    public int X { get => _config.X; set { _config.X = value; OnPropertyChanged(); RefreshOverlay(); _onChanged(); } }
    public int Y { get => _config.Y; set { _config.Y = value; OnPropertyChanged(); RefreshOverlay(); _onChanged(); } }
    public int Width { get => _config.Width; set { _config.Width = value; OnPropertyChanged(); RefreshOverlay(); _onChanged(); } }
    public int Height { get => _config.Height; set { _config.Height = value; OnPropertyChanged(); RefreshOverlay(); _onChanged(); } }

    public OcrResolvedRegion Resolve(int clientWidth, int clientHeight, bool rightAnchored = false) =>
        OcrRegionProjection.Resolve(_config, clientWidth, clientHeight, rightAnchored);

    public void SetProjectionClient(int clientWidth, int clientHeight, bool rightAnchored = false)
    {
        _projectionClientWidth = clientWidth;
        _projectionClientHeight = clientHeight;
        _rightAnchored = rightAnchored;
        RefreshOverlay();
    }

    private void RefreshOverlay()
    {
        var resolved = _projectionClientWidth > 0 && _projectionClientHeight > 0
            ? OcrRegionProjection.Resolve(_config, _projectionClientWidth, _projectionClientHeight, _rightAnchored)
            : new OcrResolvedRegion(_config.X, _config.Y, _config.Width, _config.Height);

        OverlayX = resolved.X;
        OverlayY = resolved.Y;
        OverlayWidth = resolved.Width;
        OverlayHeight = resolved.Height;
        OnPropertyChanged(nameof(OverlayX));
        OnPropertyChanged(nameof(OverlayY));
        OnPropertyChanged(nameof(OverlayWidth));
        OnPropertyChanged(nameof(OverlayHeight));
    }
}

public sealed partial class DataToolPageViewModel : PageViewModel
{
    private readonly ILogService _log;
    private readonly ITemplateOcrService _ocr;
    private readonly IWindowBindingService _binding;
    private readonly IWindowCaptureService _capture;
    private readonly ICombatRecognitionService _combat;
    private readonly IGlobalConfigService _global;
    private readonly IResolutionProfileService _resolutionProfiles;
    private readonly INavigationRecognitionService _navigation;
    private readonly IReturnPointService _returnPoints;
    private readonly IReturnPointCaptureService _returnPointCapture;
    private readonly IRuntimeDataService _runtimeData;
    private readonly IAppProfileService _profiles;

    public DataToolPageViewModel(
        IModuleCatalog catalog,
        ILogService log,
        ITemplateOcrService ocr,
        IWindowBindingService binding,
        IWindowCaptureService capture,
        ICombatRecognitionService combat,
        IGlobalConfigService global,
        IResolutionProfileService resolutionProfiles,
        INavigationRecognitionService navigation,
        IReturnPointService returnPoints,
        IReturnPointCaptureService returnPointCapture,
        IRuntimeDataService runtimeData,
        IAppProfileService profiles)
    {
        _log = log;
        _ocr = ocr;
        _binding = binding;
        _capture = capture;
        _combat = combat;
        _global = global;
        _resolutionProfiles = resolutionProfiles;
        _navigation = navigation;
        _returnPoints = returnPoints;
        _returnPointCapture = returnPointCapture;
        _runtimeData = runtimeData;
        _profiles = profiles;

        foreach (var m in catalog.ByGroup("数据")) Tools.Add(m);
        BuildCombatRegions();
        BuildNavigationRegions();
        SelectedCombatRegion = CombatRegions.FirstOrDefault();
        SelectedNavigationRegion = NavigationRegions.FirstOrDefault();
        _returnPoints.Changed += (_, _) => UiThread.Dispatch(() =>
        {
            BuildNavigationRegions();
            SelectedNavigationRegion = NavigationRegions.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedNavigationRegion));
        });
        _binding.CurrentChanged += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(HasBoundWindow));
            RefreshCombatRecognitionCommand.NotifyCanExecuteChanged();
            RefreshSelectedRegionPreviewCommand.NotifyCanExecuteChanged();
            RefreshClientPreviewCommand.NotifyCanExecuteChanged();
            SaveCurrentResolutionCombatProfileCommand.NotifyCanExecuteChanged();
            RefreshNavigationRecognitionCommand.NotifyCanExecuteChanged();
            RefreshSelectedNavigationRegionPreviewCommand.NotifyCanExecuteChanged();
            CaptureCurrentReturnPointCommand.NotifyCanExecuteChanged();
            if (_binding.Current is null)
            {
                ClientPreview = null;
                CombatPreview = null;
                NavigationPreview = null;
                ClientPreviewWidth = 0;
                ClientPreviewHeight = 0;
                SelectedRegionText = "-";
                SelectedNavigationRegionText = "-";
                NavigationMapText = "-";
                NavigationCoordinateText = "-";
                NavigationParsedCoordinate = "-";
            }
        });
        RefreshOcrStatus();
        RefreshDataIndex();
    }

    public override string Title => "数据";
    public override string Subtitle => "校准与日志。";

    public ObservableCollection<ModuleDescriptor> Tools { get; } = new();
    public ObservableCollection<CombatRegionViewModel> CombatRegions { get; } = new();
    public ObservableCollection<CombatRegionViewModel> NavigationRegions { get; } = new();

    public bool HasBoundWindow => _binding.Current is not null;

    [ObservableProperty] private int _dictionaryFileCount;
    [ObservableProperty] private int _loadedGlyphCount;
    [ObservableProperty] private int _renderableGlyphCount;
    [ObservableProperty] private int _loadedSourceCount;
    [ObservableProperty] private string _ocrStatus = "未加载字库";
    [ObservableProperty] private int _dataIndexItemCount;
    [ObservableProperty] private int _dataIndexAvailableCount;
    [ObservableProperty] private int _dataIndexSkippedCount;
    [ObservableProperty] private int _dataIndexMissingCount;
    [ObservableProperty] private string _dataIndexStatus = "未读取索引";
    [ObservableProperty] private string _dataIndexUpdatedAt = "-";
    [ObservableProperty] private bool _isRefreshingGates;
    [ObservableProperty] private string _gateStatus = "未刷新";
    [ObservableProperty] private string _targetSampleGateStatus = "未刷新";
    [ObservableProperty] private string _targetSampleGateDetail = "需要刷新后查看样本覆盖。";
    [ObservableProperty] private string _petDeployGateStatus = "未刷新";
    [ObservableProperty] private string _petDeployGateDetail = "需要刷新后查看当前方案。";
    [ObservableProperty] private string _returnPointGateStatus = "未刷新";
    [ObservableProperty] private string _returnPointGateDetail = "需要刷新后查看回点门禁。";
    [ObservableProperty] private string _stablePhaseGateStatus = "未刷新";
    [ObservableProperty] private string _stablePhaseGateDetail = "需要刷新后查看当前可测阶段。";

    [ObservableProperty] private CombatRegionViewModel? _selectedCombatRegion;
    [ObservableProperty] private CombatRegionViewModel? _selectedNavigationRegion;
    [ObservableProperty] private ImageSource? _clientPreview;
    [ObservableProperty] private int _clientPreviewWidth;
    [ObservableProperty] private int _clientPreviewHeight;
    [ObservableProperty] private ImageSource? _combatPreview;
    [ObservableProperty] private ImageSource? _navigationPreview;
    [ObservableProperty] private string _combatOcrStatus = "待绑定。";
    [ObservableProperty] private string _navigationOcrStatus = "待绑定。";
    [ObservableProperty] private string _resolutionProfileStatus = "待绑定。";
    [ObservableProperty] private string _selectedRegionText = "-";
    [ObservableProperty] private string _selectedNavigationRegionText = "-";
    [ObservableProperty] private string _targetNameText = "-";
    [ObservableProperty] private string _targetHpText = "-";
    [ObservableProperty] private string _playerHpText = "-";
    [ObservableProperty] private string _playerMpText = "-";
    [ObservableProperty] private string _petHpText = "-";
    [ObservableProperty] private string _targetHpPercent = "-";
    [ObservableProperty] private string _playerHpPercent = "-";
    [ObservableProperty] private string _playerMpPercent = "-";
    [ObservableProperty] private string _petHpPercent = "-";
    [ObservableProperty] private string _navigationMapText = "-";
    [ObservableProperty] private string _navigationCoordinateText = "-";
    [ObservableProperty] private string _navigationParsedCoordinate = "-";
    [ObservableProperty] private string _returnPointCaptureStatus = "可录入。";

    partial void OnSelectedCombatRegionChanged(CombatRegionViewModel? value)
    {
        if (value is not null)
            CombatOcrStatus = $"当前预览区域：{value.Name}";
    }

    partial void OnSelectedNavigationRegionChanged(CombatRegionViewModel? value)
    {
        if (value is not null)
            NavigationOcrStatus = $"当前预览区域：{value.Name}";
    }

    [RelayCommand]
    private void Open(ModuleDescriptor? descriptor)
    {
        if (descriptor is null) return;
        _log.Info(descriptor.Title, "已打开。");
    }

    [RelayCommand]
    private void OpenTargetSampler()
    {
        if (ToolLauncher.TryLaunchTargetSampler(out var detail))
            _log.Info("目标采集", "已打开目标采集器：绑定窗口后可采集玩家、怪物、BOSS、宠物和空目标样本。");
        else
            _log.Warn("目标采集", detail);
    }

    [RelayCommand]
    private async Task RefreshStabilityGatesAsync()
    {
        if (IsRefreshingGates)
            return;

        IsRefreshingGates = true;
        GateStatus = "刷新中...";
        try
        {
            var target = await ToolLauncher.RunPowerShellScriptAsync("Run-TargetSampleReadiness.ps1");
            ApplyTargetSampleGate(target);

            var profile = _profiles.CurrentProfileName
                          ?? _profiles.ListProfileNames().FirstOrDefault()
                          ?? "方案-0621-0916";
            var pet = await ToolLauncher.RunPowerShellScriptAsync(
                "Run-PetDeployReadiness.ps1",
                new[] { "-Profile", profile });
            ApplyPetDeployGate(pet, profile);

            var returnPoint = await ToolLauncher.RunPowerShellScriptAsync(
                "Run-ReturnPointReadiness.ps1",
                new[] { "-Profile", profile });
            ApplyReturnPointGate(returnPoint);

            var stable = await ToolLauncher.RunPowerShellScriptAsync(
                "Run-StableTestReadiness.ps1",
                new[] { "-SkipBuild" });
            ApplyStablePhaseGate(stable);

            GateStatus = StablePhaseGateStatus;
            _log.Info("稳定门禁", GateStatus);
        }
        finally
        {
            IsRefreshingGates = false;
        }
    }

    private void ApplyReturnPointGate(ToolRunResult result)
    {
        if (!result.Success)
        {
            ReturnPointGateStatus = "刷新失败";
            ReturnPointGateDetail = ShortToolError(result);
            _log.Warn("回点门禁", ReturnPointGateDetail);
            return;
        }

        var level = Regex.Match(result.Output, @"(?m)^Level=(.+)$");
        var summary = Regex.Match(result.Output, @"(?m)^Summary=(.+)$");
        var next = Regex.Match(result.Output, @"(?m)^NextAction=(.+)$");
        var scene = Regex.Match(result.Output, @"(?m)^Scene=(.+)$");

        ReturnPointGateStatus = level.Success
            ? level.Groups[1].Value.Trim() switch
            {
                "Ready" => "已就绪",
                "Warning" => "需确认",
                "Blocked" => "已阻断",
                _ => "已刷新"
            }
            : "已刷新";

        var parts = new List<string>();
        if (summary.Success) parts.Add(summary.Groups[1].Value.Trim());
        if (scene.Success) parts.Add(scene.Groups[1].Value.Trim());
        if (next.Success) parts.Add(next.Groups[1].Value.Trim());
        ReturnPointGateDetail = parts.Count == 0
            ? "已刷新，但未解析到回点摘要。"
            : string.Join(" ", parts);
    }

    private void ApplyStablePhaseGate(ToolRunResult result)
    {
        if (!result.Success)
        {
            StablePhaseGateStatus = "刷新失败";
            StablePhaseGateDetail = ShortToolError(result);
            _log.Warn("稳定门禁", StablePhaseGateDetail);
            return;
        }

        var overall = Regex.Match(result.Output, @"(?m)^Overall=([A-Z]+)\s*$");
        var currentGate = Regex.Match(result.Output, @"(?m)^CurrentGate=([A-Z_]+)\s*$");
        var conclusion = Regex.Match(result.Output, @"(?m)^CurrentConclusion=(.+)$");
        var basic = Regex.Match(result.Output, @"(?m)^basic-hang-stability=(True|False) \(([^)]+)\)\s*$");
        var ret = Regex.Match(result.Output, @"(?m)^return-point-dry-run=(True|False) \(([^)]+)\)\s*$");
        var full = Regex.Match(result.Output, @"(?m)^full-p0-stability=(True|False) \(([^)]+)\)\s*$");

        var overallValue = overall.Success ? overall.Groups[1].Value : "UNKNOWN";
        StablePhaseGateStatus = overallValue switch
        {
            "PASS" => "完整可测",
            "PARTIAL" => "部分可测",
            "BLOCKED" => "不可稳定测",
            _ => "已刷新"
        };

        var parts = new List<string>();
        if (currentGate.Success)
            parts.Add($"阶段 {currentGate.Groups[1].Value}");
        if (basic.Success)
            parts.Add($"基础挂机 {ReadyText(basic.Groups[1].Value)}");
        if (ret.Success)
            parts.Add($"回点干跑 {ReadyText(ret.Groups[1].Value)}");
        if (full.Success)
            parts.Add($"完整P0 {ReadyText(full.Groups[1].Value)}");
        if (conclusion.Success)
            parts.Add(conclusion.Groups[1].Value.Trim());

        StablePhaseGateDetail = parts.Count == 0
            ? "已刷新，但未解析到阶段摘要。"
            : string.Join("；", parts) + "。";
    }

    [RelayCommand]
    private void LoadImportedDictionaries()
    {
        var files = _runtimeData.EnumerateDictionaryFiles().ToList();
        if (files.Count == 0)
        {
            OcrStatus = "未找到字库。";
            _log.Warn("TemplateOcr", "未找到字库。");
            return;
        }

        DictionaryFileCount = files.Count;
        foreach (var file in files)
        {
            try
            {
                _ocr.LoadDictionary(file);
            }
            catch
            {
                _log.Warn("TemplateOcr", "字库加载失败。");
            }
        }

        RefreshOcrStatus();
    }

    [RelayCommand]
    private void RefreshDataIndex()
    {
        var summary = _runtimeData.ReadPublicIndex();
        DataIndexItemCount = summary.Total;
        DataIndexAvailableCount = summary.Available;
        DataIndexSkippedCount = summary.Skipped;
        DataIndexMissingCount = summary.Missing;
        DataIndexUpdatedAt = summary.UpdatedAt;
        DataIndexStatus = summary.Status;
        _log.Info("运行数据", "索引已刷新。");
    }

    private void ApplyTargetSampleGate(ToolRunResult result)
    {
        if (!result.Success)
        {
            TargetSampleGateStatus = "刷新失败";
            TargetSampleGateDetail = ShortToolError(result);
            _log.Warn("目标样本", TargetSampleGateDetail);
            return;
        }

        var ready = Regex.Match(result.Output, @"(?m)^RecommendedReady=(True|False)\s*$");
        var counts = Regex.Match(
            result.Output,
            @"(?m)^Counts=player:(\d+)/(\d+), monster:(\d+)/(\d+), boss:(\d+)/(\d+), pet:(\d+)/(\d+), none:(\d+)/(\d+)\s*$");

        var isReady = ready.Success && ready.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
        TargetSampleGateStatus = isReady ? "已就绪" : "未就绪";

        if (counts.Success)
        {
            var playerMissing = Missing(counts.Groups[1].Value, counts.Groups[2].Value);
            var bossMissing = Missing(counts.Groups[5].Value, counts.Groups[6].Value);
            var petMissing = Missing(counts.Groups[7].Value, counts.Groups[8].Value);
            var noneMissing = Missing(counts.Groups[9].Value, counts.Groups[10].Value);
            TargetSampleGateDetail =
                $"玩家 {counts.Groups[1].Value}/{counts.Groups[2].Value}，怪物 {counts.Groups[3].Value}/{counts.Groups[4].Value}，BOSS {counts.Groups[5].Value}/{counts.Groups[6].Value}，宠物 {counts.Groups[7].Value}/{counts.Groups[8].Value}，空目标 {counts.Groups[9].Value}/{counts.Groups[10].Value}。";

            if (!isReady)
            {
                var missing = new List<string>();
                if (playerMissing > 0) missing.Add($"玩家差 {playerMissing}");
                if (bossMissing > 0) missing.Add($"BOSS差 {bossMissing}");
                if (petMissing > 0) missing.Add($"宠物差 {petMissing}");
                if (noneMissing > 0) missing.Add($"空目标差 {noneMissing}");
                if (missing.Count > 0)
                    TargetSampleGateDetail += " 缺口：" + string.Join("、", missing) + "。";
            }
        }
        else
        {
            TargetSampleGateDetail = "已刷新，但未解析到计数，请查看日志证据。";
        }
    }

    private void ApplyPetDeployGate(ToolRunResult result, string profile)
    {
        if (!result.Success)
        {
            PetDeployGateStatus = "刷新失败";
            PetDeployGateDetail = ShortToolError(result);
            _log.Warn("宠物出战", PetDeployGateDetail);
            return;
        }

        var runtime = Regex.Match(result.Output, @"(?m)^RuntimeReady=(True|False)\s*$");
        var mode = Regex.Match(result.Output, @"(?m)^Mode=(\d+) Hotkey='([^']*)' Click=(\d+),bottom-(\d+)\s*$");
        var ready = runtime.Success && runtime.Groups[1].Value.Equals("True", StringComparison.OrdinalIgnoreCase);
        PetDeployGateStatus = ready ? "已就绪" : "未就绪";

        if (mode.Success)
        {
            var modeText = mode.Groups[1].Value == "1" ? "点击" : "热键";
            var hotkey = string.IsNullOrWhiteSpace(mode.Groups[2].Value) ? "-" : mode.Groups[2].Value;
            PetDeployGateDetail =
                $"方案 {profile}：模式 {modeText}，热键 {hotkey}，点击 X={mode.Groups[3].Value} / 底距={mode.Groups[4].Value}。";
            if (!ready)
                PetDeployGateDetail += " 需要在目标采集器中标定宠物出战按钮，或配置明确热键。";
        }
        else
        {
            PetDeployGateDetail = $"方案 {profile} 已刷新，但未解析到运行配置。";
        }
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void RefreshClientPreview()
    {
        var window = _binding.Current;
        if (window is null)
        {
            ClientPreview = null;
            ClientPreviewWidth = 0;
            ClientPreviewHeight = 0;
            ResolutionProfileStatus = "未绑定。";
            CombatOcrStatus = "未绑定。";
            return;
        }

        var capture = _capture.CaptureClient(window.Hwnd);
        ClientPreview = CaptureImaging.ToBitmapSource(capture);
        ClientPreviewWidth = capture?.Width ?? 0;
        ClientPreviewHeight = capture?.Height ?? 0;
        ApplyPreviewProjection(ClientPreviewWidth, ClientPreviewHeight);
        RefreshResolutionProfileStatus(ClientPreviewWidth, ClientPreviewHeight);
        CombatOcrStatus = capture is null
            ? "截图失败。"
            : "预览已刷新。";

        if (capture is not null)
            _log.Info("CombatOcr", "预览已刷新。", window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void SaveCurrentResolutionCombatProfile()
    {
        var window = _binding.Current;
        if (window is null)
        {
            ResolutionProfileStatus = "未绑定。";
            return;
        }

        var capture = _capture.CaptureClient(window.Hwnd);
        if (capture is null)
        {
            ResolutionProfileStatus = "截图失败。";
            _log.Warn("ResolutionProfile", ResolutionProfileStatus, window.Id);
            return;
        }

        ClientPreview = CaptureImaging.ToBitmapSource(capture);
        ClientPreviewWidth = capture.Width;
        ClientPreviewHeight = capture.Height;
        ApplyPreviewProjection(ClientPreviewWidth, ClientPreviewHeight);

        _resolutionProfiles.SaveCombatProfile(
            capture.Width,
            capture.Height,
            _global.Current.CombatRecognition,
            "识别校准");

        RefreshResolutionProfileStatus(capture.Width, capture.Height);
        _log.Info("ResolutionProfile", ResolutionProfileStatus, window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void RefreshCombatRecognition()
    {
        var window = _binding.Current;
        if (window is null)
        {
            CombatOcrStatus = "未绑定。";
            return;
        }

        RefreshSelectedRegionPreview();
        var result = _combat.Recognize(window, _global.Current.CombatRecognition);
        ApplyCombatResult(result);
        CombatOcrStatus = result.HasTarget
            ? "识别完成。"
            : "未识别。";
        _log.Info("CombatOcr", CombatOcrStatus, window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void RefreshSelectedRegionPreview()
    {
        var window = _binding.Current;
        var region = SelectedCombatRegion;
        if (window is null || region is null)
        {
            CombatPreview = null;
            return;
        }

        var client = _capture.CaptureClient(window.Hwnd);
        if (client is not null)
            region.SetProjectionClient(client.Width, client.Height, rightAnchored: true);

        var effective = client is null
            ? new OcrResolvedRegion(region.X, region.Y, region.Width, region.Height)
            : region.Resolve(client.Width, client.Height, rightAnchored: true);
        var capture = effective.IsEmpty
            ? null
            : _capture.CaptureRegion(window.Hwnd, effective.X, effective.Y, effective.Width, effective.Height);
        CombatPreview = CaptureImaging.ToBitmapSource(capture);
        if (CombatPreview is null)
        {
            CombatOcrStatus = $"区域截图失败：{region.Name}";
            SelectedRegionText = "-";
            return;
        }

        SelectedRegionText = _ocr.Recognize(capture!).Trim();
        if (string.IsNullOrWhiteSpace(SelectedRegionText))
            SelectedRegionText = "-";
        CombatOcrStatus = "预览完成。";
        _log.Info("CombatOcr", CombatOcrStatus, window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void RefreshNavigationRecognition()
    {
        var window = _binding.Current;
        if (window is null)
        {
            NavigationOcrStatus = "未绑定。";
            return;
        }

        RefreshSelectedNavigationRegionPreview();
        var result = _navigation.Recognize(window, _returnPoints.Current.NavigationRecognition);
        NavigationMapText = Blank(result.MapNameText);
        NavigationCoordinateText = Blank(result.CoordinateText);
        NavigationParsedCoordinate = result.HasCoordinate ? $"{result.X},{result.Y}" : "-";
        NavigationOcrStatus = result.HasCoordinate
            ? $"坐标 {NavigationParsedCoordinate}"
            : "未识别。";
        _log.Info("ReturnPointOcr", NavigationOcrStatus, window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void RefreshSelectedNavigationRegionPreview()
    {
        var window = _binding.Current;
        var region = SelectedNavigationRegion;
        if (window is null || region is null)
        {
            NavigationPreview = null;
            return;
        }

        var capture = _capture.CaptureRegion(window.Hwnd, region.X, region.Y, region.Width, region.Height);
        NavigationPreview = CaptureImaging.ToBitmapSource(capture);
        if (NavigationPreview is null)
        {
            NavigationOcrStatus = $"区域截图失败：{region.Name}";
            SelectedNavigationRegionText = "-";
            return;
        }

        if (region.Key == "return-coordinate")
        {
            var result = _navigation.Recognize(window, _returnPoints.Current.NavigationRecognition);
            SelectedNavigationRegionText = Blank(result.CoordinateText);
            NavigationParsedCoordinate = result.HasCoordinate ? $"{result.X},{result.Y}" : "-";
        }
        else
        {
            SelectedNavigationRegionText = _ocr.Recognize(capture!).Trim();
            if (string.IsNullOrWhiteSpace(SelectedNavigationRegionText))
                SelectedNavigationRegionText = "-";
        }

        NavigationOcrStatus = "预览完成。";
        _log.Info("ReturnPointOcr", NavigationOcrStatus, window.Id);
    }

    [RelayCommand(CanExecute = nameof(HasBoundWindow))]
    private void CaptureCurrentReturnPoint()
    {
        var result = _returnPointCapture.CaptureAndAddCurrentPoint(ReturnPointKind.FixedFarm, "当前坐标");
        ReturnPointCaptureStatus = result.Message;
        if (result.Navigation is not null)
        {
            NavigationMapText = Blank(result.Navigation.MapNameText);
            NavigationCoordinateText = Blank(result.Navigation.CoordinateText);
            NavigationParsedCoordinate = result.Navigation.HasCoordinate
                ? $"{result.Navigation.X},{result.Navigation.Y}"
                : "-";
        }
    }

    [RelayCommand]
    private void ApplyNavigationRegions()
    {
        _returnPoints.Save(_returnPoints.Current.ProfileName);
        _log.Info("ReturnPoint", "坐标识别区已保存。");
    }

    [RelayCommand]
    private void ApplyCombatRegions()
    {
        _global.NotifyChanged();
        _log.Info("Combat", "识别区已应用。");
    }

    private void RefreshResolutionProfileStatus(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            ResolutionProfileStatus = "未获取尺寸。";
            return;
        }

        ResolutionProfileStatus = _resolutionProfiles.TryGetCombatProfile(clientWidth, clientHeight, out var profile)
            ? $"{clientWidth}x{clientHeight} 已校准"
            : $"{clientWidth}x{clientHeight} 未校准";
    }

    private void ApplyCombatResult(CombatRecognitionResult result)
    {
        TargetNameText = Blank(result.TargetNameText);
        TargetHpText = Blank(result.TargetHpText);
        PlayerHpText = Blank(result.PlayerHpText);
        PlayerMpText = Blank(result.PlayerMpText);
        PetHpText = Blank(result.PetHpText);
        TargetHpPercent = Percent(result.TargetHpPercent);
        PlayerHpPercent = Percent(result.PlayerHpPercent);
        PlayerMpPercent = Percent(result.PlayerMpPercent);
        PetHpPercent = Percent(result.PetHpPercent);
    }

    private void RefreshOcrStatus()
    {
        LoadedGlyphCount = _ocr.LoadedGlyphCount;
        RenderableGlyphCount = _ocr.RenderableGlyphCount;
        LoadedSourceCount = _ocr.LoadedSources.Count;
        OcrStatus = LoadedGlyphCount == 0
            ? "未加载字库"
            : $"已加载 {LoadedSourceCount} 组，{RenderableGlyphCount} 条可用";
    }

    private void BuildCombatRegions()
    {
        var cfg = _global.Current.CombatRecognition;
        CombatRegions.Clear();
        CombatRegions.Add(new CombatRegionViewModel("target-name", "目标名称", cfg.TargetName, _global.NotifyChanged));
        CombatRegions.Add(new CombatRegionViewModel("target-hp", "目标血量", cfg.TargetHp, _global.NotifyChanged));
        CombatRegions.Add(new CombatRegionViewModel("player-hp", "角色血量", cfg.PlayerHp, _global.NotifyChanged));
        CombatRegions.Add(new CombatRegionViewModel("player-mp", "角色蓝量", cfg.PlayerMp, _global.NotifyChanged));
        CombatRegions.Add(new CombatRegionViewModel("pet-hp", "宠物血量", cfg.PetHp, _global.NotifyChanged));
    }

    private void ApplyPreviewProjection(int clientWidth, int clientHeight)
    {
        foreach (var region in CombatRegions)
            region.SetProjectionClient(clientWidth, clientHeight);
        foreach (var region in NavigationRegions)
            region.SetProjectionClient(clientWidth, clientHeight, rightAnchored: true);
    }

    private void BuildNavigationRegions()
    {
        var cfg = _returnPoints.Current.NavigationRecognition;
        NavigationRegions.Clear();
        NavigationRegions.Add(new CombatRegionViewModel("return-map", "返点地图名", cfg.MapName, () => { }));
        NavigationRegions.Add(new CombatRegionViewModel("return-coordinate", "返点坐标", cfg.Coordinate, () => { }));
        ApplyPreviewProjection(ClientPreviewWidth, ClientPreviewHeight);
    }

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string Percent(int? value) => value is null ? "-" : value + "%";
    private static int Missing(string ready, string required) =>
        Math.Max(0, int.Parse(required) - int.Parse(ready));
    private static string ReadyText(string value) =>
        value.Equals("True", StringComparison.OrdinalIgnoreCase) ? "可测" : "未就绪";
    private static string ShortToolError(ToolRunResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        text = text.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? $"脚本退出码 {result.ExitCode}"
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"脚本退出码 {result.ExitCode}";
    }

}
