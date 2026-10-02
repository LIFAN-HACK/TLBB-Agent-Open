using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public enum NavKind { Home, Mode, Data }
public enum TopTabKind { Home, ModeGroup, Data }

internal static class UiRouteKeys
{
    public const string ActivityCenter = "ui.activity-center";
}

public sealed class NavEntryViewModel
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required NavKind Kind { get; init; }
    public string Icon { get; init; } = "\uE8D4";
    public string Description { get; init; } = string.Empty;
    public bool CanRun { get; init; }
    public bool IsAvailable { get; init; }
    public bool ShowPendingBadge => !IsAvailable;
    public string AvailabilityText => IsAvailable ? "可用" : "待接入";
    public ModuleDescriptor? Descriptor { get; init; }
    public string RunLabel => string.IsNullOrWhiteSpace(Group) ? Title : $"{Group} / {Title}";

    public override string ToString() => Title;
}

public sealed class TopTabViewModel
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required TopTabKind Kind { get; init; }
    public string Icon { get; init; } = "\uE8D4";
    public ObservableCollection<NavEntryViewModel> Modes { get; } = new();
}

public sealed partial class WindowSessionViewModel : ObservableObject
{
    public WindowSessionViewModel(BoundGameWindow window)
    {
        Window = window;
    }

    public BoundGameWindow Window { get; private set; }
    public string Key => Window.Id;
    public string Title => string.IsNullOrWhiteSpace(RoleDisplayName)
        ? string.IsNullOrWhiteSpace(Window.Title) ? Window.ProcessName : Window.Title
        : RoleDisplayName;
    public string Subtitle => IsCurrent
        ? string.IsNullOrWhiteSpace(RoleDisplayName) ? IdentityStatusText : $"已绑定 · {IdentityStatusText}"
        : "待选择";
    public string ClientSize => Window.ClientSizeText;
    public string PidText => Window.Pid > 0 ? Window.Pid.ToString() : "-";
    public bool HasAvatar => Avatar is not null;
    public string SelectButtonText => IsCurrent ? "已绑定" : "选择";
    public bool CanSelect => !IsCurrent;

    [ObservableProperty] private ImageSource? _avatar;
    [ObservableProperty] private RunState _runState = RunState.Idle;
    [ObservableProperty] private string _selectedModeKey = FeatureKeys.HangNormal;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private string _characterProfileId = string.Empty;
    [ObservableProperty] private string _roleSessionId = string.Empty;
    [ObservableProperty] private string _roleDisplayName = string.Empty;
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _identityStatusText = "正在识别角色";

    partial void OnRoleDisplayNameChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    partial void OnIdentityStatusTextChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SelectButtonText));
        OnPropertyChanged(nameof(CanSelect));
    }

    public void Refresh(BoundGameWindow window, bool isCurrent, ImageSource? avatar = null)
    {
        Window = window;
        IsCurrent = isCurrent;
        if (avatar is not null)
            Avatar = avatar;
        OnPropertyChanged(nameof(Window));
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ClientSize));
        OnPropertyChanged(nameof(PidText));
        OnPropertyChanged(nameof(HasAvatar));
    }

    public void ApplyIdentity(CharacterIdentityRecord identity, string roleSessionId)
    {
        CharacterProfileId = identity.CharacterProfileId;
        RoleSessionId = roleSessionId;
        RoleDisplayName = identity.DisplayName;
        ProfileName = identity.ProfileName;
        IdentityStatusText = "配置已自动加载";
    }

    public void MarkIdentityPending(string status)
    {
        CharacterProfileId = string.Empty;
        RoleSessionId = string.Empty;
        RoleDisplayName = string.Empty;
        ProfileName = string.Empty;
        IdentityStatusText = status;
    }
}

public sealed record RoleIdentityCandidateViewModel(
    string CharacterProfileId,
    string DisplayName,
    string ProfileName,
    string ScoreText);

public partial class MainViewModel : ObservableObject
{
    private const string Module = "App";
    private readonly IWindowBindingService _binding;
    private readonly IWindowCaptureService _capture;
    private readonly ILatestFrameService _latestFrames;
    private readonly IPerceptionHub _perceptionHub;
    private readonly IRoleIdentityService _roleIdentity;
    private readonly IGameHotkeyProfileService _gameHotkeys;
    private readonly IRoleSessionManager _roleSessions;
    private readonly ILogService _log;
    private readonly IUserSessionService _userSession;
    private readonly IAppProfileService _profiles;
    private readonly IGlobalConfigService _global;
    private readonly ITargetPolicyCatalogService _targetPolicyCatalog;
    private readonly ISkillCooldownRecognitionService _skillCooldown;
    private readonly IMainlineReadinessService _mainlineReadiness;
    private readonly IShoutConfigService _shoutConfig;
    private readonly IShoutController _shoutController;
    private readonly IAutoLoginConfigService _autoLoginConfig;
    private readonly IAutoLoginController _autoLoginController;
    private readonly IAutoReconnectConfigService _autoReconnectConfig;
    private readonly IAutoReconnectController _autoReconnectController;
    private readonly IInputService _input;
    private readonly IReturnPointService _returnPoints;
    private readonly IReturnPointCaptureService _returnPointCapture;
    private readonly IReturnPointReadinessService _returnPointReadiness;
    private readonly IReturnNavigationRecordImportService _returnNavigationRecordImport;
    private readonly IHorseBanditConfigService _horseBandit;
    private readonly INpcGrabConfigService _npcGrab;
    private readonly IAutoRebelConfigService _autoRebel;
    private readonly IAutoMonkConfigService _autoMonk;
    private readonly IDungeonConfigService _dungeon;
    private readonly ILifeCollectConfigService _lifeCollect;
    private readonly ITradeConfigService _trade;
    private readonly IMaterialCraftConfigService _materialCraft;
    private readonly ISchoolTaskConfigService _schoolTask;
    private readonly ITreasureMapConfigService _treasureMap;
    private readonly IGuildContributionConfigService _guildContribution;
    private readonly IUtilityConfigService _utility;
    private readonly IBagCleanupService _bagCleanup;
    private readonly IBagCleanupReportService _bagCleanupReport;
    private readonly IInventoryRecognitionService _inventory;
    private readonly IItemPolicyService _itemPolicy;
    private readonly IQuestionBankService _questionBank;
    private readonly IRuntimeDataService _runtimeData;
    private readonly IAutomationRegistry _registry;
    private readonly IModuleCatalog _catalog;
    private readonly DispatcherTimer _inputYieldStatusTimer;
    private readonly GlobalConfigPageViewModel _homePage;
    private readonly DataToolPageViewModel _dataPage;
    private ActivityCenterPageViewModel? _activityPage;
    private readonly Dictionary<string, PageViewModel> _pageCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _autoSaveCts;
    private CancellationTokenSource? _roleIdentityCts;
    private BoundGameWindow? _pendingRoleWindow;
    private WindowSessionViewModel? _pendingRoleSession;
    private nint _hotkeyWindowHandle;
    private bool _suppressAutoSave;
    private bool _suppressSessionModeSync;
    private readonly HashSet<IAutomationController> _observedControllers = new();

    public MainViewModel(
        IWindowBindingService binding,
        ILogService log,
        IUserSessionService userSession,
        IAppProfileService profiles,
        IGlobalConfigService global,
        ITargetPolicyCatalogService targetPolicyCatalog,
        ISkillCooldownRecognitionService skillCooldown,
        IMainlineReadinessService mainlineReadiness,
        IShoutConfigService shoutConfig,
        IShoutController shoutController,
        IAutoLoginConfigService autoLoginConfig,
        IAutoLoginController autoLoginController,
        IAutoReconnectConfigService autoReconnectConfig,
        IAutoReconnectController autoReconnectController,
        IInputService input,
        IReturnPointService returnPoints,
        IReturnPointCaptureService returnPointCapture,
        IReturnPointReadinessService returnPointReadiness,
        IReturnNavigationRecordImportService returnNavigationRecordImport,
        IHorseBanditConfigService horseBandit,
        INpcGrabConfigService npcGrab,
        IAutoRebelConfigService autoRebel,
        IAutoMonkConfigService autoMonk,
        IDungeonConfigService dungeon,
        ILifeCollectConfigService lifeCollect,
        ITradeConfigService trade,
        IMaterialCraftConfigService materialCraft,
        ISchoolTaskConfigService schoolTask,
        ITreasureMapConfigService treasureMap,
        IGuildContributionConfigService guildContribution,
        IUtilityConfigService utility,
        IBagCleanupService bagCleanup,
        IBagCleanupReportService bagCleanupReport,
        IInventoryRecognitionService inventory,
        IItemPolicyService itemPolicy,
        IQuestionBankService questionBank,
        IModuleCatalog catalog,
        IAutomationRegistry registry,
        ITemplateOcrService ocr,
        IWindowCaptureService capture,
        ILatestFrameService latestFrames,
        IPerceptionHub perceptionHub,
        IRoleIdentityService roleIdentity,
        IGameHotkeyProfileService gameHotkeys,
        IRoleSessionManager roleSessions,
        ICombatRecognitionService combat,
        IResolutionProfileService resolutionProfiles,
        INavigationRecognitionService navigation,
        IRuntimeDataService runtimeData,
        BindingPanelViewModel bindingPanel,
        LogPanelViewModel logPanel)
    {
        _binding = binding;
        _capture = capture;
        _latestFrames = latestFrames;
        _perceptionHub = perceptionHub;
        _roleIdentity = roleIdentity;
        _gameHotkeys = gameHotkeys;
        _roleSessions = roleSessions;
        _log = log;
        _userSession = userSession;
        _profiles = profiles;
        _global = global;
        _targetPolicyCatalog = targetPolicyCatalog;
        _skillCooldown = skillCooldown;
        _mainlineReadiness = mainlineReadiness;
        _shoutConfig = shoutConfig;
        _shoutController = shoutController;
        _autoLoginConfig = autoLoginConfig;
        _autoLoginController = autoLoginController;
        _autoReconnectConfig = autoReconnectConfig;
        _autoReconnectController = autoReconnectController;
        _input = input;
        _returnPoints = returnPoints;
        _returnPointCapture = returnPointCapture;
        _returnPointReadiness = returnPointReadiness;
        _returnNavigationRecordImport = returnNavigationRecordImport;
        _horseBandit = horseBandit;
        _npcGrab = npcGrab;
        _autoRebel = autoRebel;
        _autoMonk = autoMonk;
        _dungeon = dungeon;
        _lifeCollect = lifeCollect;
        _trade = trade;
        _materialCraft = materialCraft;
        _schoolTask = schoolTask;
        _treasureMap = treasureMap;
        _guildContribution = guildContribution;
        _utility = utility;
        _bagCleanup = bagCleanup;
        _bagCleanupReport = bagCleanupReport;
        _inventory = inventory;
        _itemPolicy = itemPolicy;
        _questionBank = questionBank;
        _runtimeData = runtimeData;
        _catalog = catalog;
        _registry = registry;
        BindingPanel = bindingPanel;
        LogPanel = logPanel;

        _homePage = new GlobalConfigPageViewModel(global);
        _dataPage = new DataToolPageViewModel(catalog, log, ocr, binding, capture, combat, global, resolutionProfiles, navigation, returnPoints, returnPointCapture, runtimeData, profiles);

        _global.Changed += (_, _) => UiThread.Dispatch(() =>
        {
            RefreshGlobalSwitches();
            ApplyInputDispatchMode();
            UpdateInputYieldStatus();
            RefreshMainlineReadinessStatus();
            UpdateRunAvailability();
            ScheduleAutoSave();
        });
        _shoutConfig.Changed += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(Shout));
            ApplyShoutRuntime();
            ScheduleAutoSave();
        });
        _autoLoginConfig.Changed += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(AutoLogin));
            ApplyAutoLoginRuntime();
            ScheduleAutoSave();
        });
        _autoReconnectConfig.Changed += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(AutoReconnect));
            ApplyAutoReconnectRuntime();
            ScheduleAutoSave();
        });
        _returnPoints.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _horseBandit.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _npcGrab.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _autoRebel.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _autoMonk.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _dungeon.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _lifeCollect.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _trade.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _materialCraft.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _schoolTask.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _treasureMap.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _guildContribution.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _utility.Changed += (_, _) => UiThread.Dispatch(ScheduleAutoSave);
        _userSession.Changed += (_, _) => UiThread.Dispatch(RefreshUserSession);

        _binding.CurrentChanged += (_, window) => UiThread.Dispatch(() =>
        {
            var nextHandle = window?.Hwnd ?? nint.Zero;
            if (_hotkeyWindowHandle != nint.Zero && _hotkeyWindowHandle != nextHandle)
                _gameHotkeys.Unbind(_hotkeyWindowHandle);
            _hotkeyWindowHandle = nextHandle;

            var selectedSession = AddOrUpdateSession(window);
            if (selectedSession is not null && !ReferenceEquals(SelectedSession, selectedSession))
                SelectedSession = selectedSession;

            WindowScanStatus = window is null ? "尚未选择游戏窗口" : "游戏窗口已选择";

            _registry.AttachAll(window);
            if (window is not null)
            {
                if (selectedSession is not null)
                    BeginRoleIdentityResolution(window, selectedSession);
            }
            UpdateInputYieldStatus();
            RefreshMainlineReadinessStatus();
            UpdateRunAvailability();
        });

        _registry.ControllerRegistered += OnControllerRegistered;
        foreach (var controller in _registry.Controllers)
            ObserveController(controller);

        BuildTabs();
        RefreshProfiles();
        EnsureCurrentProfile();
        LoadSelectedProfileAtStartup();
        RefreshGlobalSwitches();
        ApplyInputDispatchMode();
        _inputYieldStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _inputYieldStatusTimer.Tick += (_, _) => UpdateInputYieldStatus();
        _inputYieldStatusTimer.Start();
        UpdateInputYieldStatus();
        RefreshMainlineReadinessStatus();
        SelectedTab = Tabs.FirstOrDefault();
        UpdateRunAvailability();
    }

    public BindingPanelViewModel BindingPanel { get; }
    public LogPanelViewModel LogPanel { get; }
    public ObservableCollection<TopTabViewModel> Tabs { get; } = new();
    public ObservableCollection<NavEntryViewModel> CurrentModeTags { get; } = new();
    public ObservableCollection<NavEntryViewModel> RunModes { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<string> MainlineReadinessItems { get; } = new();
    public ObservableCollection<WindowSessionViewModel> Sessions { get; } = new();
    public ObservableCollection<RoleIdentityCandidateViewModel> RoleIdentityCandidates { get; } = new();

    [ObservableProperty] private TopTabViewModel? _selectedTab;
    [ObservableProperty] private NavEntryViewModel? _selectedMode;
    [ObservableProperty] private WindowSessionViewModel? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private NavEntryViewModel? _selectedRunMode;

    [ObservableProperty] private PageViewModel? _currentDetail;
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private RunState _runState = RunState.Idle;
    [ObservableProperty] private string _inputYieldStatusText = "玩家操作保护：待绑定";
    [ObservableProperty] private string _inputYieldStatusDetail = "绑定窗口后显示让控状态。";
    [ObservableProperty] private bool _inputYieldActive;
    [ObservableProperty] private string _mainlineReadinessTitle = "挂机就绪：待检查";
    [ObservableProperty] private string _mainlineReadinessSummary = "绑定窗口后显示关键配置缺口。";
    [ObservableProperty] private bool _mainlineReadinessHasDetails;
    [ObservableProperty] private bool _mainlineReadinessHasErrors;
    [ObservableProperty] private bool _mainlineReadinessHasWarnings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canStart;

    [ObservableProperty] private string _autoSaveStatusText = "修改会自动保存";
    [ObservableProperty] private bool _isAutoSaving;
    [ObservableProperty] private string _windowScanStatus = "扫描并选择游戏窗口";
    [ObservableProperty] private bool _isAccountMenuOpen;
    [ObservableProperty] private bool _isRoleEnrollmentRequired;
    [ObservableProperty] private bool _isRoleIdentityBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmRoleEnrollmentCommand))]
    private string _roleEnrollmentName = string.Empty;
    [ObservableProperty] private string _roleEnrollmentTitle = "确认当前角色";
    [ObservableProperty] private string _roleEnrollmentMessage = "首次绑定需要确认一次，后续将自动加载该角色配置。";

    public bool HasModeTags => CurrentModeTags.Count > 1;
    public bool HasSessions => Sessions.Count > 0;
    public bool IsSessionSwitchAllowed => true;
    public string UserDisplayName => string.IsNullOrWhiteSpace(_userSession.Current.DisplayName)
        ? "未登录"
        : _userSession.Current.DisplayName;
    public string UserStatusText => string.IsNullOrWhiteSpace(_userSession.Current.StatusText)
        ? "登录 / 账户"
        : _userSession.Current.StatusText;
    public bool IsUserLoggedIn => _userSession.Current.State == UserSessionState.SignedIn;
    public bool IsUserLoginAvailable => _userSession.CanBeginLogin
                                        && _userSession.Current.State is UserSessionState.SignedOut or UserSessionState.Expired or UserSessionState.Error;
    public string WorkspaceTitle => SelectedMode?.Title ?? SelectedTab?.Title ?? "角色设置";
    public string WorkspaceSubtitle => SelectedMode?.Description ?? "当前配置跟随所选角色方案，并在修改后自动保存。";
    public string ActiveRoleTitle => SelectedSession is null
        ? "尚未绑定游戏窗口"
        : string.IsNullOrWhiteSpace(SelectedSession.RoleDisplayName)
            ? "正在识别角色"
            : SelectedSession.RoleDisplayName;
    public string ActiveRoleSubtitle => SelectedSession is null
        ? "拖动准星或扫描正在运行的游戏窗口"
        : SelectedSession.Subtitle;

    public string SelectedModeTitle =>
        SelectedRunMode?.Title ?? "未选择模式";
    public string StartButtonText => SelectedRunMode is null
        ? "请选择任务"
        : SelectedRunMode.CanRun
            ? $"启动{SelectedRunMode.Title}"
            : $"{SelectedRunMode.Title}未就绪";

    public bool AutoReconnect
    {
        get => _autoReconnectConfig.Current.Enabled;
        set
        {
            if (_autoReconnectConfig.Current.Enabled == value) return;
            _autoReconnectConfig.Current.Enabled = value;
            _global.Current.AutoReconnect = value;
            OnPropertyChanged();
            _autoReconnectConfig.NotifyChanged();
            _global.NotifyChanged();
        }
    }

    public bool AutoLogin
    {
        get => _autoLoginConfig.Current.Enabled;
        set
        {
            if (_autoLoginConfig.Current.Enabled == value) return;
            _autoLoginConfig.Current.Enabled = value;
            _global.Current.AutoLogin = value;
            OnPropertyChanged();
            _autoLoginConfig.NotifyChanged();
            _global.NotifyChanged();
        }
    }

    public bool Shout
    {
        get => _shoutConfig.Current.Enabled;
        set
        {
            if (_shoutConfig.Current.Enabled == value) return;
            _shoutConfig.Current.Enabled = value;
            OnPropertyChanged();
            _shoutConfig.NotifyChanged();
        }
    }

    private IAutomationController? SelectedController
    {
        get
        {
            var window = _binding.Current;
            return window is not null
                   && SelectedRunMode is not null
                   && SelectedRunMode.CanRun
                   && SelectedRunMode.Key != "hang.return"
                ? _registry.Find(window, ControllerKeyForMode(SelectedRunMode.Key))
                : null;
        }
    }

    private void BuildTabs()
    {
        Tabs.Add(new TopTabViewModel
        {
            Key = "home",
            Title = "角色设置",
            Kind = TopTabKind.Home,
            Icon = "\uE77B"
        });

        AddModeTab("hang", "挂机", "\uE768", new[]
        {
            FeatureKeys.HangNormal,
            FeatureKeys.HangBoss,
            FeatureKeys.HangDungeon,
            FeatureKeys.HangCleanup,
            FeatureKeys.HangMaterial
        });
        AddUiModeTab("activity", "活动", "\uE787", new NavEntryViewModel
        {
            Key = UiRouteKeys.ActivityCenter,
            Title = "活动设置",
            Group = "活动",
            Kind = NavKind.Mode,
            Icon = "\uE787",
            IsAvailable = false
        });
        AddModeTab("life", "生活", "\uE9D9", new[]
        {
            FeatureKeys.LifeTreasureMap,
            FeatureKeys.LifeMining,
            FeatureKeys.LifeHerb,
            FeatureKeys.LifeTrade
        });
        Tabs.First(tab => tab.Key == "life").Modes.Add(new NavEntryViewModel
        {
            Key = FeatureKeys.LifeShout,
            Title = "喊话",
            Group = "生活",
            Kind = NavKind.Mode,
            Icon = "\uE8BD",
            Description = "按当前角色方案循环发送聊天内容。",
            CanRun = false,
            IsAvailable = _shoutController.IsChannelRoutingReady
        });
        AddModeTab("task", "任务", "\uE9D5", new[]
        {
            FeatureKeys.TaskDaily
        });
        AddModeTab("quiz", "答题", "\uE897", new[]
        {
            FeatureKeys.QuizAssistant
        });

#if DEBUG
        if (string.Equals(Environment.GetEnvironmentVariable("TLBB_AGENT_DEV_UI"), "1", StringComparison.Ordinal))
        {
            var data = new TopTabViewModel
            {
                Key = "data",
                Title = "开发工具",
                Kind = TopTabKind.Data,
                Icon = "\uE943"
            };
            foreach (var module in _catalog.ByGroup("数据"))
                data.Modes.Add(CreateNavEntry(module, "开发工具", NavKind.Data));
            Tabs.Add(data);
        }
#endif

        RunModes.Clear();
        foreach (var mode in Tabs
                     .Where(t => t.Kind == TopTabKind.ModeGroup)
                     .SelectMany(t => t.Modes)
                     .Where(mode => mode.Descriptor is not null))
            RunModes.Add(mode);
        SelectedRunMode = RunModes.FirstOrDefault(m => m.Key == FeatureKeys.HangNormal) ?? RunModes.FirstOrDefault();
    }

    private void AddUiModeTab(string key, string title, string icon, NavEntryViewModel mode)
    {
        var tab = new TopTabViewModel
        {
            Key = key,
            Title = title,
            Kind = TopTabKind.ModeGroup,
            Icon = icon
        };
        tab.Modes.Add(mode);
        Tabs.Add(tab);
    }

    private void AddModeTab(string key, string title, string icon, IEnumerable<string> moduleKeys)
    {
        var tab = new TopTabViewModel
        {
            Key = key,
            Title = title,
            Kind = TopTabKind.ModeGroup,
            Icon = icon
        };

        foreach (var moduleKey in moduleKeys)
        {
            var module = _catalog.Modules.FirstOrDefault(item => item.Key == moduleKey);
            if (module is null || module.Status == ModuleIntegrationStatus.NotIntegrated)
                continue;
            tab.Modes.Add(CreateNavEntry(module, title, NavKind.Mode));
        }

        if (tab.Modes.Count > 0)
            Tabs.Add(tab);
    }

    private static NavEntryViewModel CreateNavEntry(ModuleDescriptor module, string displayGroup, NavKind kind)
    {
        var (title, icon, description) = module.Key switch
        {
            FeatureKeys.HangNormal => ("挂机打怪", "\uE7FC", string.Empty),
            FeatureKeys.HangBoss => ("BOSS 模式", "\uEA18", string.Empty),
            FeatureKeys.HangDungeon => ("副本模式", "\uE7EE", string.Empty),
            FeatureKeys.HangCleanup => ("自动清包", "\uE74D", string.Empty),
            FeatureKeys.HangMaterial => ("材料加工", "\uE9F5", string.Empty),
            FeatureKeys.ActivityXiaoxiao => ("自动宵小", "\uE8B0", string.Empty),
            FeatureKeys.ActivityRebel => ("自动反贼", "\uE7BA", string.Empty),
            FeatureKeys.ActivityMonk => ("自动和尚", "\uE7BF", string.Empty),
            FeatureKeys.LifeTreasureMap => ("挖图", "\uE707", string.Empty),
            FeatureKeys.LifeMining => ("挖矿", "\uE9F9", string.Empty),
            FeatureKeys.LifeHerb => ("采药", "\uE8F1", string.Empty),
            FeatureKeys.LifeTrade => ("跑商", "\uE7BF", string.Empty),
            FeatureKeys.TaskDaily => ("日常任务", "\uE8D7", string.Empty),
            FeatureKeys.QuizAssistant => ("答题助手", "\uE897", string.Empty),
            _ => (module.Title, "\uE8D4", module.Description)
        };

        return new NavEntryViewModel
        {
            Key = module.Key,
            Title = title,
            Group = displayGroup,
            Kind = kind,
            Icon = icon,
            Description = description,
            CanRun = module.Key is FeatureKeys.HangNormal or FeatureKeys.HangBoss or FeatureKeys.HangMaterial,
            IsAvailable = kind == NavKind.Data
                          || module.Key is FeatureKeys.HangNormal or FeatureKeys.HangBoss or FeatureKeys.HangMaterial,
            Descriptor = module
        };
    }

    partial void OnSelectedTabChanged(TopTabViewModel? value)
    {
        CurrentModeTags.Clear();
        SelectedMode = null;

        if (value is null)
        {
            CurrentDetail = _homePage;
            return;
        }

        foreach (var mode in value.Modes)
            CurrentModeTags.Add(mode);

        OnPropertyChanged(nameof(HasModeTags));

        if (value.Kind == TopTabKind.Home)
        {
            CurrentDetail = _homePage;
        }
        else if (value.Kind == TopTabKind.Data)
        {
            CurrentDetail = _dataPage;
            SelectedMode = CurrentModeTags.FirstOrDefault();
        }
        else
        {
            SelectedMode = CurrentModeTags.FirstOrDefault();
        }

        UpdateRunAvailability();
        OnPropertyChanged(nameof(SelectedModeTitle));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceSubtitle));
    }

    partial void OnSelectedRunModeChanged(NavEntryViewModel? value)
    {
        if (!_suppressSessionModeSync && value is not null && SelectedSession is not null)
            SelectedSession.SelectedModeKey = value.Key;

        if (value?.Descriptor is not null && SelectedMode?.Key != value.Key)
        {
            var tab = Tabs.FirstOrDefault(t => t.Modes.Any(m => m.Key == value.Key));
            if (tab is not null && !ReferenceEquals(SelectedTab, tab))
                SelectedTab = tab;

            var mode = CurrentModeTags.FirstOrDefault(m => m.Key == value.Key);
            if (mode is not null && !ReferenceEquals(SelectedMode, mode))
                SelectedMode = mode;
        }

        UpdateRunAvailability();
        OnPropertyChanged(nameof(SelectedModeTitle));
        OnPropertyChanged(nameof(StartButtonText));
        RefreshMainlineReadinessStatus();
    }

    partial void OnSelectedModeChanged(NavEntryViewModel? value)
    {
        if (SelectedTab?.Kind == TopTabKind.Home)
        {
            CurrentDetail = _homePage;
        }
        else if (SelectedTab?.Kind == TopTabKind.Data)
        {
            CurrentDetail = _dataPage;
        }
        else if (value is not null)
        {
            CurrentDetail = GetOrCreateModePage(value);

            if (value.Descriptor is not null
                && RunModes.Contains(value)
                && !ReferenceEquals(SelectedRunMode, value))
            {
                SelectedRunMode = value;
            }
        }

        UpdateRunAvailability();
        OnPropertyChanged(nameof(SelectedModeTitle));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(WorkspaceSubtitle));
        RefreshMainlineReadinessStatus();
    }

    private PageViewModel GetOrCreateModePage(NavEntryViewModel value)
    {
        if (_pageCache.TryGetValue(value.Key, out var cached))
            return cached;

        PageViewModel page = value.Key switch
        {
            FeatureKeys.HangNormal => new HangPageViewModel(
                value.Descriptor!,
                _registry.Find(value.Key),
                new ReturnPointPageViewModel(ReturnPointDescriptor(), null, _returnPoints, _returnPointCapture, _returnPointReadiness, _returnNavigationRecordImport, _runtimeData, _log),
                _global,
                _returnPoints,
                OpenHomeTab,
                _binding,
                _latestFrames,
                _perceptionHub,
                _gameHotkeys),
            "hang.return" => new ReturnPointPageViewModel(ReturnPointDescriptor(), null, _returnPoints, _returnPointCapture, _returnPointReadiness, _returnNavigationRecordImport, _runtimeData, _log),
            FeatureKeys.HangBoss => new BossModePageViewModel(_global, _targetPolicyCatalog, OpenHomeTab),
            FeatureKeys.HangCleanup => new AutoCleanupPageViewModel(_utility),
            UiRouteKeys.ActivityCenter => _activityPage ??= new ActivityCenterPageViewModel(_global),
            FeatureKeys.HangDungeon => new DungeonModePageViewModel(),
            FeatureKeys.LifeTrade => CreateFeaturePreview(value.Key),
            FeatureKeys.HangMaterial => new MaterialProcessingPageViewModel(
                _materialCraft,
                _registry.Find(FeatureKeys.HangMaterial)),
            FeatureKeys.LifeTreasureMap => CreateFeaturePreview(value.Key),
            FeatureKeys.LifeMining => CreateFeaturePreview(value.Key),
            FeatureKeys.LifeHerb => CreateFeaturePreview(value.Key),
            FeatureKeys.LifeShout => new ShoutPageViewModel(_shoutConfig, _shoutController),
            FeatureKeys.TaskDaily => new TaskAutomationPageViewModel(_global),
            FeatureKeys.QuizAssistant => new QuizAssistantPageViewModel(),
            _ => new ModeConfigPageViewModel(value.Descriptor!, _registry.Find(ControllerKeyForMode(value.Key)), _log)
        };

        _pageCache[value.Key] = page;
        return page;
    }

    private static AgentFeaturePageViewModel CreateFeaturePreview(string key) => key switch
    {
        FeatureKeys.HangBoss => Pending("BOSS 模式", string.Empty),
        FeatureKeys.HangDungeon => Pending("副本模式", string.Empty),
        FeatureKeys.HangCleanup => Pending("自动清包", string.Empty),
        FeatureKeys.HangMaterial => Pending("材料加工", string.Empty),
        FeatureKeys.ActivityXiaoxiao => Pending("自动宵小", string.Empty),
        FeatureKeys.ActivityRebel => Pending("自动反贼", string.Empty),
        FeatureKeys.ActivityMonk => Pending("自动和尚", string.Empty),
        FeatureKeys.LifeTrade => Pending("跑商", string.Empty),
        FeatureKeys.LifeTreasureMap => Pending("挖图", string.Empty),
        FeatureKeys.LifeMining => Pending("挖矿", string.Empty),
        FeatureKeys.LifeHerb => Pending("采药", string.Empty),
        FeatureKeys.TaskDaily => Pending("日常任务", string.Empty),
        FeatureKeys.QuizAssistant => Pending("答题助手", string.Empty),
        _ => Pending("功能准备中", "等待后端能力接入。")
    };

    private static AgentFeaturePageViewModel Pending(string title, string _) => new(
        title,
        string.Empty,
        "未就绪",
        string.Empty);

    partial void OnSelectedSessionChanged(WindowSessionViewModel? value)
    {
        RestoreSelectedSessionMode(value);
        OnPropertyChanged(nameof(ActiveRoleTitle));
        OnPropertyChanged(nameof(ActiveRoleSubtitle));
        UpdateRunAvailability();
    }

    private void RestoreSelectedSessionMode(WindowSessionViewModel? session)
    {
        if (session is null)
            return;

        var mode = RunModes.FirstOrDefault(item => item.Key == session.SelectedModeKey)
                   ?? RunModes.FirstOrDefault(item => item.Key == FeatureKeys.HangNormal)
                   ?? RunModes.FirstOrDefault();
        if (mode is null || ReferenceEquals(mode, SelectedRunMode))
            return;

        _suppressSessionModeSync = true;
        try
        {
            SelectedRunMode = mode;
        }
        finally
        {
            _suppressSessionModeSync = false;
        }
    }

    private WindowSessionViewModel? AddOrUpdateSession(BoundGameWindow? window)
    {
        if (window is null)
        {
            foreach (var session in Sessions)
                session.Refresh(session.Window, false);
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(ActiveRoleTitle));
            OnPropertyChanged(nameof(ActiveRoleSubtitle));
            return null;
        }

        var existing = Sessions.FirstOrDefault(s => s.Window.Hwnd == window.Hwnd);
        if (existing is null)
        {
            existing = new WindowSessionViewModel(window);
            Sessions.Add(existing);
            OnPropertyChanged(nameof(HasSessions));
        }

        foreach (var session in Sessions)
            session.Refresh(
                session.Window.Hwnd == window.Hwnd ? window : session.Window,
                ReferenceEquals(session, existing),
                ReferenceEquals(session, existing) ? BindingPanel.Avatar : null);

        OnPropertyChanged(nameof(ActiveRoleTitle));
        OnPropertyChanged(nameof(ActiveRoleSubtitle));

        return existing;
    }

    private static string ControllerKeyForMode(string key) => key switch
    {
        FeatureKeys.HangBoss => FeatureKeys.HangNormal,
        _ => key
    };

    private void PrepareSelectedRunMode()
    {
        if (SelectedRunMode is null)
            return;

        var changed = false;
        if (SelectedRunMode.Key == FeatureKeys.HangBoss)
        {
            changed |= _targetPolicyCatalog.MergeInto(
                _global.Current.Targeting.Policy,
                TargetPolicyCategoryKind.Boss) > 0;
            if (!_global.Current.Targeting.Policy.Enabled)
            {
                _global.Current.Targeting.Policy.Enabled = true;
                changed = true;
            }

            if (!_global.Current.Targeting.L2TargetNameEnabled)
            {
                _global.Current.Targeting.L2TargetNameEnabled = true;
                changed = true;
            }

            var category = _global.Current.Targeting.Policy.Categories
                .First(item => item.Category == TargetPolicyCategoryKind.Boss);
            if (!category.Enabled)
            {
                category.Enabled = true;
                changed = true;
            }

            if (_global.Current.Targeting.SwitchMode != TargetSwitchMode.BossWhitelist)
            {
                _global.Current.Targeting.SwitchMode = TargetSwitchMode.BossWhitelist;
                changed = true;
            }
        }
        else if (SelectedRunMode.Key == FeatureKeys.HangNormal
                 && _global.Current.Targeting.SwitchMode != TargetSwitchMode.KillThenSwitch)
        {
            _global.Current.Targeting.SwitchMode = TargetSwitchMode.KillThenSwitch;
            changed = true;
        }

        if (changed)
            _global.NotifyChanged();
    }

    private void OpenHomeTab()
    {
        SelectedTab = Tabs.FirstOrDefault(t => t.Kind == TopTabKind.Home);
    }

    private ModuleDescriptor ReturnPointDescriptor() =>
        _catalog.Modules.FirstOrDefault(m => m.Key == "hang.return")
        ?? new ModuleDescriptor
        {
            Key = "hang.return",
            Group = "挂机",
            Title = "返点配置",
            Priority = "P0",
            SourceModule = "配置",
            Description = "坐标、土灵、死亡、卡点恢复。",
            Status = ModuleIntegrationStatus.Partial
        };

    private void RefreshProfiles()
    {
        var selected = SelectedProfile;
        var current = _profiles.CurrentProfileName;
        Profiles.Clear();
        foreach (var name in _profiles.ListProfileNames())
            Profiles.Add(name);
        SelectedProfile =
            current is not null && Profiles.Contains(current) ? current :
            selected is not null && Profiles.Contains(selected) ? selected :
            Profiles.FirstOrDefault();
    }

    private void EnsureCurrentProfile()
    {
        if (!string.IsNullOrWhiteSpace(SelectedProfile))
            return;

        var name = $"方案-{DateTime.Now:MMdd-HHmm}";
        _profiles.SaveAll(name);
        RefreshProfiles();
        SelectedProfile = name;
        AutoSaveStatusText = $"已新建方案 · {name}";
        if (_binding.Current is not null)
            _profiles.RememberWindowProfile(_binding.Current, name);
    }

    private void LoadSelectedProfileAtStartup()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile) || !Profiles.Contains(SelectedProfile))
            return;

        _suppressAutoSave = true;
        try
        {
            _profiles.LoadAll(SelectedProfile);
        }
        finally
        {
            _suppressAutoSave = false;
        }

        RefreshProfiles();
    }

    private void BeginRoleIdentityResolution(BoundGameWindow window, WindowSessionViewModel session)
    {
        _roleIdentityCts?.Cancel();
        _roleIdentityCts?.Dispose();
        _roleIdentityCts = new CancellationTokenSource();
        _autoSaveCts?.Cancel();
        _autoSaveCts = null;
        IsAutoSaving = false;
        _suppressAutoSave = true;
        _pendingRoleWindow = window;
        _pendingRoleSession = session;
        session.MarkIdentityPending("正在识别角色");
        IsRoleIdentityBusy = true;
        IsRoleEnrollmentRequired = false;
        RoleIdentityCandidates.Clear();
        AutoSaveStatusText = "正在识别角色配置";
        OnPropertyChanged(nameof(ActiveRoleTitle));
        OnPropertyChanged(nameof(ActiveRoleSubtitle));
        _ = ResolveRoleIdentityAsync(window, session, _roleIdentityCts.Token);
    }

    private async Task ResolveRoleIdentityAsync(
        BoundGameWindow window,
        WindowSessionViewModel session,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _roleIdentity.ResolveAsync(window, cancellationToken).ConfigureAwait(false);
            UiThread.Dispatch(() => ApplyRoleIdentityResolution(window, session, resolution));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Dispatch(() =>
            {
                IsRoleIdentityBusy = false;
                IsRoleEnrollmentRequired = true;
                RoleEnrollmentTitle = "需要确认当前角色";
                RoleEnrollmentMessage = "自动识别未完成，请确认角色名称。";
                session.MarkIdentityPending("等待角色确认");
                _log.Warn("RoleIdentity", $"角色识别失败：{ex.Message}", window.Id);
            });
        }
    }

    private void ApplyRoleIdentityResolution(
        BoundGameWindow window,
        WindowSessionViewModel session,
        RoleIdentityResolution resolution)
    {
        if ((_binding.Current?.Hwnd ?? nint.Zero) != window.Hwnd
            || !ReferenceEquals(_pendingRoleSession, session))
        {
            return;
        }

        IsRoleIdentityBusy = false;
        RoleIdentityCandidates.Clear();
        foreach (var candidate in resolution.Candidates)
        {
            RoleIdentityCandidates.Add(new RoleIdentityCandidateViewModel(
                candidate.CharacterProfileId,
                candidate.DisplayName,
                candidate.ProfileName,
                $"匹配 {candidate.Score:P0}"));
        }

        if (resolution.IsMatched && resolution.Match is not null)
        {
            ActivateMatchedRole(window, session, resolution.Match);
            return;
        }

        session.MarkIdentityPending(
            resolution.Status == RoleIdentityResolutionStatus.Ambiguous
                ? "发现相似角色，等待确认"
                : "等待首次确认");
        RoleEnrollmentName = resolution.Observation.SuggestedName;
        RoleEnrollmentTitle = resolution.Status switch
        {
            RoleIdentityResolutionStatus.Ambiguous => "请选择当前角色",
            RoleIdentityResolutionStatus.Unavailable => "角色画面需要确认",
            _ => "首次绑定角色"
        };
        RoleEnrollmentMessage = resolution.Status == RoleIdentityResolutionStatus.Ambiguous
            ? "检测到多个相似角色。请选择历史角色，或输入名称建立新角色。"
            : "确认一次角色名称后，后续启动会通过视觉指纹自动加载配置。";
        IsRoleEnrollmentRequired = true;
        AutoSaveStatusText = "等待角色确认，尚未写入配置";
        OnPropertyChanged(nameof(ActiveRoleTitle));
        OnPropertyChanged(nameof(ActiveRoleSubtitle));
        ConfirmRoleEnrollmentCommand.NotifyCanExecuteChanged();
    }

    private void ActivateMatchedRole(
        BoundGameWindow window,
        WindowSessionViewModel session,
        CharacterIdentityRecord identity)
    {
        var profileExists = _profiles.ListProfileNames().Contains(identity.ProfileName, StringComparer.Ordinal);
        if (!profileExists)
        {
            session.MarkIdentityPending("角色配置缺失，等待重建");
            RoleEnrollmentName = identity.DisplayName;
            RoleEnrollmentTitle = "角色配置需要重建";
            RoleEnrollmentMessage = "已识别到角色，但对应配置文件缺失。确认后将用当前设置重建。";
            IsRoleEnrollmentRequired = true;
            _suppressAutoSave = true;
            _log.Warn("RoleIdentity", $"角色 {identity.DisplayName} 的配置文件缺失，已阻止自动加载。", window.Id);
            return;
        }

        _suppressAutoSave = true;
        try
        {
            _profiles.LoadAll(identity.ProfileName);
            _profiles.RememberWindowProfile(window, identity.ProfileName);
            RefreshProfiles();
            SelectedProfile = identity.ProfileName;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        var roleSessionId = $"window-{window.Pid}-{window.Hwnd.ToInt64():X}";
        var hotkeyProfile = _gameHotkeys.BindToRole(window, identity.DisplayName);
        if (!hotkeyProfile.Loaded)
        {
            _log.Warn(
                "InputBinding",
                $"当前角色键位未自动接入：{hotkeyProfile.Reason}。未验证的客户端默认键不会发送。",
                window.Id);
        }

        try
        {
            _roleSessions.OpenOrUpdate(roleSessionId, identity.CharacterProfileId, window);
        }
        catch (RoleSessionOpenException ex)
        {
            _log.Warn("RoleSession", ex.Message, window.Id);
        }

        session.ApplyIdentity(identity, roleSessionId);
        _registry.RefreshConfiguration(window);
        ApplySelectedWindowAuxiliaryRuntimes();
        IsRoleEnrollmentRequired = false;
        RoleIdentityCandidates.Clear();
        AutoSaveStatusText = $"已自动加载 · {identity.DisplayName}";
        OnPropertyChanged(nameof(ActiveRoleTitle));
        OnPropertyChanged(nameof(ActiveRoleSubtitle));
        _log.Info("RoleIdentity", $"已识别角色并加载配置：{identity.DisplayName}。", window.Id);
    }

    private bool CanConfirmRoleEnrollment() =>
        IsRoleEnrollmentRequired
        && !IsRoleIdentityBusy
        && _pendingRoleWindow is not null
        && _pendingRoleSession is not null
        && !string.IsNullOrWhiteSpace(RoleEnrollmentName);

    partial void OnIsRoleIdentityBusyChanged(bool value)
    {
        ConfirmRoleEnrollmentCommand.NotifyCanExecuteChanged();
        RetryRoleIdentityCommand.NotifyCanExecuteChanged();
        UseRoleCandidateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmRoleEnrollment))]
    private async Task ConfirmRoleEnrollment()
    {
        var window = _pendingRoleWindow;
        var session = _pendingRoleSession;
        var displayName = RoleEnrollmentName.Trim();
        if (window is null || session is null || displayName.Length == 0)
            return;

        IsRoleIdentityBusy = true;
        try
        {
            var profileName = CreateUniqueRoleProfileName();
            _profiles.SaveAll(profileName);
            var resolution = await _roleIdentity.EnrollAsync(window, displayName, profileName).ConfigureAwait(false);
            UiThread.Dispatch(() =>
            {
                IsRoleIdentityBusy = false;
                if (resolution.IsMatched && resolution.Match is not null)
                    ActivateMatchedRole(window, session, resolution.Match);
                else
                    ApplyRoleIdentityResolution(window, session, resolution);
            });
        }
        catch (Exception ex)
        {
            UiThread.Dispatch(() =>
            {
                IsRoleIdentityBusy = false;
                RoleEnrollmentMessage = $"保存未完成：{ex.Message}";
                _log.Error("RoleIdentity", $"首次角色确认失败：{ex.Message}", window.Id);
            });
        }
    }

    private bool CanUseRoleCandidate(RoleIdentityCandidateViewModel? candidate) =>
        candidate is not null
        && IsRoleEnrollmentRequired
        && !IsRoleIdentityBusy
        && _pendingRoleWindow is not null
        && _pendingRoleSession is not null;

    [RelayCommand(CanExecute = nameof(CanUseRoleCandidate))]
    private async Task UseRoleCandidate(RoleIdentityCandidateViewModel? candidate)
    {
        var window = _pendingRoleWindow;
        var session = _pendingRoleSession;
        if (candidate is null || window is null || session is null)
            return;

        IsRoleIdentityBusy = true;
        try
        {
            var resolution = await _roleIdentity.ConfirmExistingAsync(
                window,
                candidate.CharacterProfileId).ConfigureAwait(false);
            UiThread.Dispatch(() =>
            {
                IsRoleIdentityBusy = false;
                if (resolution.IsMatched && resolution.Match is not null)
                    ActivateMatchedRole(window, session, resolution.Match);
                else
                    ApplyRoleIdentityResolution(window, session, resolution);
            });
        }
        catch (Exception ex)
        {
            UiThread.Dispatch(() =>
            {
                IsRoleIdentityBusy = false;
                RoleEnrollmentMessage = $"历史角色确认未完成：{ex.Message}";
            });
        }
    }

    private bool CanRetryRoleIdentity() =>
        IsRoleEnrollmentRequired
        && !IsRoleIdentityBusy
        && _pendingRoleWindow is not null
        && _pendingRoleSession is not null;

    [RelayCommand(CanExecute = nameof(CanRetryRoleIdentity))]
    private void RetryRoleIdentity()
    {
        if (_pendingRoleWindow is not null && _pendingRoleSession is not null)
            BeginRoleIdentityResolution(_pendingRoleWindow, _pendingRoleSession);
    }

    private string CreateUniqueRoleProfileName()
    {
        string profileName;
        do
        {
            profileName = $"角色档案-{Guid.NewGuid().ToString("N")[..8]}";
        } while (_profiles.ListProfileNames().Contains(profileName, StringComparer.Ordinal));
        return profileName;
    }

    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave)
            return;

        var profileName = _profiles.CurrentProfileName ?? SelectedProfile;
        if (string.IsNullOrWhiteSpace(profileName)
            || !_profiles.ListProfileNames().Contains(profileName, StringComparer.Ordinal))
        {
            return;
        }

        _autoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        var expectedHwnd = _binding.Current?.Hwnd ?? nint.Zero;
        IsAutoSaving = true;
        AutoSaveStatusText = "正在自动保存…";
        _ = AutoSaveCurrentProfileAsync(profileName, expectedHwnd, cts.Token);
    }

    private async Task AutoSaveCurrentProfileAsync(string profileName, nint expectedHwnd, CancellationToken token)
    {
        try
        {
            await Task.Delay(800, token);
            if (token.IsCancellationRequested || (_binding.Current?.Hwnd ?? nint.Zero) != expectedHwnd)
            {
                UiThread.Dispatch(() =>
                {
                    IsAutoSaving = false;
                    AutoSaveStatusText = "角色已切换，已取消旧方案保存";
                });
                return;
            }

            _profiles.SaveAll(profileName);
            UiThread.Dispatch(() =>
            {
                RefreshProfiles();
                SelectedProfile = profileName;
                if (_binding.Current is not null)
                    _profiles.RememberWindowProfile(_binding.Current, profileName);
                IsAutoSaving = false;
                AutoSaveStatusText = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
            });
        }
        catch (OperationCanceledException)
        {
            UiThread.Dispatch(() => IsAutoSaving = false);
        }
    }

    private void RefreshGlobalSwitches()
    {
        OnPropertyChanged(nameof(AutoReconnect));
        OnPropertyChanged(nameof(AutoLogin));
        OnPropertyChanged(nameof(Shout));
    }

    private void ApplyInputDispatchMode()
    {
        _input.DefaultDispatchMode = _global.Current.InputDispatchMode;
        _input.YieldToUserInput = _global.Current.YieldToManualInput;
        _input.UserInputYieldMs = _global.Current.ManualInputYieldMs;
    }

    private void UpdateInputYieldStatus()
    {
        var window = _binding.Current;
        if (window is null)
        {
            InputYieldStatusText = "玩家操作保护：待绑定";
            InputYieldStatusDetail = "绑定窗口后显示让控状态。";
            InputYieldActive = false;
            return;
        }

        ApplyInputDispatchMode();
        var status = _input.GetYieldStatus(window.Hwnd, _global.Current.InputDispatchMode);
        if (!status.Enabled)
        {
            InputYieldStatusText = "玩家操作保护：关闭";
            InputYieldStatusDetail = "自动输入不会避让人工操作。";
            InputYieldActive = false;
            return;
        }

        if (status.ShouldYield)
        {
            InputYieldStatusText = "玩家操作保护：暂停中";
            InputYieldStatusDetail = ShortInputYieldReason(status.Reason);
            InputYieldActive = true;
            return;
        }

        InputYieldStatusText = "玩家操作保护：可用";
        InputYieldStatusDetail = ShortInputYieldReason(status.Reason);
        InputYieldActive = false;
    }

    private void RefreshMainlineReadinessStatus()
    {
        MainlineReadinessItems.Clear();

        if (IsUnavailableBuiltInMonitorMode())
        {
            MainlineReadinessTitle = "挂机就绪：未就绪";
            MainlineReadinessSummary = "内挂监控未就绪。";
            MainlineReadinessHasDetails = false;
            MainlineReadinessHasErrors = true;
            MainlineReadinessHasWarnings = false;
            return;
        }

        if (!RequiresMainlineReadinessForSelectedMode())
        {
            MainlineReadinessTitle = "就绪检查：当前模式";
            MainlineReadinessSummary = "非挂机模式不检查打怪键位。";
            MainlineReadinessHasDetails = false;
            MainlineReadinessHasErrors = false;
            MainlineReadinessHasWarnings = false;
            return;
        }

        var report = _mainlineReadiness.Inspect(_global.Current, _binding.Current);
        var errors = report.Errors;
        var warnings = report.Warnings;

        MainlineReadinessHasErrors = errors.Count > 0;
        MainlineReadinessHasWarnings = warnings.Count > 0;

        if (MainlineReadinessHasErrors)
        {
            MainlineReadinessTitle = "挂机就绪：需处理";
            MainlineReadinessSummary = $"{errors.Count} 个错误，启动会被拦截。";
        }
        else if (MainlineReadinessHasWarnings)
        {
            MainlineReadinessTitle = "挂机就绪：有提示";
            MainlineReadinessSummary = $"{warnings.Count} 个配置提示，基础打怪可继续。";
        }
        else
        {
            MainlineReadinessTitle = "挂机就绪：通过";
            MainlineReadinessSummary = "关键键位与恢复动作已具备。";
        }

        foreach (var item in errors.Take(2).Concat(warnings.Take(Math.Max(0, 3 - Math.Min(2, errors.Count)))))
            MainlineReadinessItems.Add(FormatReadinessItem(item));

        MainlineReadinessHasDetails = MainlineReadinessItems.Count > 0;
    }

    private static string FormatReadinessItem(MainlineReadinessItem item)
    {
        var text = $"{item.Feature}：{item.Message}";
        if (!string.IsNullOrWhiteSpace(item.ActionArea) || !string.IsNullOrWhiteSpace(item.Action))
        {
            var area = string.IsNullOrWhiteSpace(item.ActionArea) ? "配置" : item.ActionArea;
            var action = string.IsNullOrWhiteSpace(item.Action) ? "按提示处理。" : item.Action;
            text = $"{text} 处理：{area} - {action}";
        }

        return text.Length <= 96 ? text : $"{text[..96]}...";
    }

    private static string ShortInputYieldReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "最近没有人工操作。";

        return reason.Length <= 42 ? reason : $"{reason[..42]}...";
    }

    private void ApplyShoutRuntime()
    {
        var window = _binding.Current;
        if (!_shoutConfig.Current.Enabled)
        {
            StopWindowAuxiliaryController(window, FeatureKeys.LifeShout);
            return;
        }

        if (window is null)
        {
            _log.Warn(Module, "请先绑定窗口。");
            return;
        }

        var controller = _registry.Prepare(window, FeatureKeys.LifeShout) as IShoutController;
        if (controller is not null && controller.RunState is not RunState.Running)
            controller.Start();
    }

    private void ApplySelectedWindowAuxiliaryRuntimes()
    {
        ApplyShoutRuntime();
        ApplyAutoLoginRuntime();
        ApplyAutoReconnectRuntime();
    }

    private void ApplyAutoReconnectRuntime()
    {
        var window = _binding.Current;
        if (!_autoReconnectConfig.Current.Enabled)
        {
            StopWindowAuxiliaryController(window, FeatureKeys.UtilityAutoReconnect);
            return;
        }

        if (window is null)
        {
            _log.Warn(Module, "请先绑定窗口。");
            return;
        }

        var controller = _registry.Prepare(window, FeatureKeys.UtilityAutoReconnect) as IAutoReconnectController;
        if (controller is not null
            && controller.RunState is not RunState.Running and not RunState.Reconnecting)
        {
            controller.Start();
        }
    }

    private void ApplyAutoLoginRuntime()
    {
        var window = _binding.Current;
        if (!_autoLoginConfig.Current.Enabled)
        {
            StopWindowAuxiliaryController(window, FeatureKeys.UtilityAutoLogin);
            return;
        }

        if (window is null)
        {
            _log.Warn(Module, "请先绑定窗口。");
            return;
        }

        var controller = _registry.Prepare(window, FeatureKeys.UtilityAutoLogin) as IAutoLoginController;
        if (controller is not null && controller.RunState is not RunState.Running)
            controller.Start();
    }

    private void StopWindowAuxiliaryController(BoundGameWindow? window, string key)
    {
        if (window is null)
            return;

        var controller = _registry.FindForWindow(window)
            .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        if (controller is not null && IsActiveState(controller.RunState))
            controller.Stop();
    }

    private void UpdateRunAvailability()
    {
        var window = _binding.Current;
        PrepareSelectedRunMode();
        var controllerKey = SelectedRunMode is null
            ? string.Empty
            : ControllerKeyForMode(SelectedRunMode.Key);
        var controller = window is null || controllerKey.Length == 0
            ? null
            : _registry.Find(window, controllerKey);
        CanStart = window is not null
                   && window.IsAutomationAllowed
                   && controller is not null
                   && IsStartableState(controller.RunState)
                   && !IsUnavailableBuiltInMonitorMode();

        if (window is null)
            RunState = RunState.Idle;
        else if (controller is not null)
            RunState = controller.RunState is RunState.Idle && window.IsAutomationAllowed ? RunState.Ready : controller.RunState;
        else
            RunState = window.IsAutomationAllowed ? RunState.Ready : RunState.Idle;

        foreach (var session in Sessions)
            session.RunState = ResolveSessionRunState(session);

        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        StopAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSessionSwitchAllowed));
    }

    private RunState ResolveSessionRunState(WindowSessionViewModel session)
    {
        var controllers = _registry.FindForWindow(session.Window);
        var active = controllers.FirstOrDefault(controller => IsActiveState(controller.RunState));
        if (active is not null)
            return active.RunState;

        if (ReferenceEquals(session, SelectedSession))
            return RunState;

        if (controllers.Any(controller => controller.RunState == RunState.Error))
            return RunState.Error;
        if (controllers.Any(controller => controller.RunState == RunState.Stopped))
            return RunState.Stopped;
        return session.Window.IsAutomationAllowed ? RunState.Ready : RunState.Idle;
    }

    private void OnControllerStateChanged()
    {
        UpdateRunAvailability();
        OnPropertyChanged(nameof(IsSessionSwitchAllowed));
    }

    private void OnControllerRegistered(object? sender, AutomationControllerRegisteredEventArgs e) =>
        UiThread.Dispatch(() =>
        {
            ObserveController(e.Controller);
            UpdateRunAvailability();
        });

    private void ObserveController(IAutomationController controller)
    {
        if (!_observedControllers.Add(controller))
            return;
        controller.StateChanged += (_, _) => UiThread.Dispatch(OnControllerStateChanged);
    }

    private static bool IsStartableState(RunState state) =>
        state is RunState.Idle or RunState.Ready or RunState.Stopped or RunState.Paused or RunState.Error;

    private static bool IsActiveState(RunState state) =>
        state is RunState.Running or RunState.Paused or RunState.Recovering or RunState.DeathReturn or RunState.Reconnecting or RunState.Switching;

    private void StopCompetingMainModes(BoundGameWindow window, IAutomationController selected)
    {
        foreach (var item in _registry.FindForWindow(window))
        {
            if (ReferenceEquals(item, selected))
                continue;
            if (item.Key is FeatureKeys.LifeShout
                or FeatureKeys.UtilityAutoLogin
                or FeatureKeys.UtilityAutoReconnect)
            {
                continue;
            }
            if (IsActiveState(item.RunState))
                item.Stop();
        }
    }

    [RelayCommand]
    private void ScanGameWindows()
    {
        var windows = _binding.DiscoverGameWindows();
        var discoveredHandles = windows.Select(window => window.Hwnd).ToHashSet();
        var currentHandle = _binding.Current?.Hwnd ?? nint.Zero;

        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            var session = Sessions[index];
            if (session.Window.Hwnd != currentHandle && !discoveredHandles.Contains(session.Window.Hwnd))
            {
                _registry.StopWindow(session.Window);
                _input.ClearWindowInputConfiguration(session.Window.Hwnd);
                Sessions.RemoveAt(index);
            }
        }

        OnPropertyChanged(nameof(HasSessions));

        if (windows.Count == 0)
        {
            WindowScanStatus = currentHandle == nint.Zero ? "未发现游戏窗口" : "当前窗口已绑定";
            _log.Warn(Module, "未找到正在运行的游戏窗口。");
            return;
        }

        foreach (var window in windows)
        {
            ImageSource? avatar = null;
            try
            {
                avatar = CaptureImaging.ToBitmapSource(_capture.CaptureAvatar(window.Hwnd));
            }
            catch
            {
            }

            AddOrUpdateDiscoveredSession(window, avatar);
        }

        WindowScanStatus = currentHandle == nint.Zero
            ? $"发现 {windows.Count} 个窗口，请选择"
            : $"发现 {windows.Count} 个窗口";
        _log.Info(Module, $"已发现 {windows.Count} 个游戏窗口。", _binding.Current?.Id ?? "-");
    }

    private void AddOrUpdateDiscoveredSession(BoundGameWindow window, ImageSource? avatar)
    {
        var existing = Sessions.FirstOrDefault(session => session.Window.Hwnd == window.Hwnd);
        if (existing is null)
        {
            existing = new WindowSessionViewModel(window);
            Sessions.Add(existing);
            OnPropertyChanged(nameof(HasSessions));
        }

        existing.Refresh(window, _binding.Current?.Hwnd == window.Hwnd, avatar);
    }

    [RelayCommand]
    private void SelectGameWindow(WindowSessionViewModel? session)
    {
        if (session is null || session.IsCurrent)
            return;

        _autoSaveCts?.Cancel();
        _autoSaveCts = null;
        IsAutoSaving = false;
        AutoSaveStatusText = "正在切换角色方案";
        WindowScanStatus = "正在选择游戏窗口…";

        try
        {
            var bound = _binding.BindByHandle(session.Window.Hwnd);
            if (bound is not null)
                return;

            WindowScanStatus = "窗口已关闭，请重新扫描";
            if (!session.IsCurrent)
            {
                _registry.StopWindow(session.Window);
                _input.ClearWindowInputConfiguration(session.Window.Hwnd);
                Sessions.Remove(session);
            }
            OnPropertyChanged(nameof(HasSessions));
        }
        catch
        {
            WindowScanStatus = "窗口切换未完成，请重试";
            _log.Error(Module, "窗口切换未完成，请重试。", _binding.Current?.Id ?? "-");
        }
    }

    [RelayCommand]
    private void OpenRoleSettings()
    {
        IsAccountMenuOpen = false;
        OpenHomeTab();
    }

    [RelayCommand]
    private void ToggleAccountMenu() => IsAccountMenuOpen = !IsAccountMenuOpen;

    private bool CanBeginUserLogin() => IsUserLoginAvailable;

    [RelayCommand(CanExecute = nameof(CanBeginUserLogin))]
    private async Task BeginUserLogin()
    {
        await _userSession.BeginLoginAsync();
        IsAccountMenuOpen = false;
    }

    private void RefreshUserSession()
    {
        OnPropertyChanged(nameof(UserDisplayName));
        OnPropertyChanged(nameof(UserStatusText));
        OnPropertyChanged(nameof(IsUserLoggedIn));
        OnPropertyChanged(nameof(IsUserLoginAvailable));
        BeginUserLoginCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewProfile()
    {
        var name = $"方案-{DateTime.Now:MMdd-HHmm}";
        _profiles.SaveAll(name);
        RefreshProfiles();
        SelectedProfile = name;
        AutoSaveStatusText = $"已新建方案 · {name}";
        if (_binding.Current is not null)
            _profiles.RememberWindowProfile(_binding.Current, name);
    }

    [RelayCommand]
    private void SaveProfile()
    {
        var name = SelectedProfile ?? $"方案-{DateTime.Now:MMdd-HHmm}";
        _profiles.SaveAll(name);
        RefreshProfiles();
        SelectedProfile = name;
        if (_binding.Current is not null)
            _profiles.RememberWindowProfile(_binding.Current, name);
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var newName = SelectedProfile?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            return;

        var oldName = _profiles.CurrentProfileName ?? Profiles.FirstOrDefault() ?? "";
        _profiles.RenameAll(oldName, newName);
        RefreshProfiles();
        SelectedProfile = newName;
        AutoSaveStatusText = $"已更名方案 · {newName}";
        if (_binding.Current is not null)
            _profiles.RememberWindowProfile(_binding.Current, newName);
    }

    [RelayCommand]
    private void LoadProfile()
    {
        if (SelectedProfile is null) return;
        _autoSaveCts?.Cancel();
        _autoSaveCts = null;
        IsAutoSaving = false;
        _suppressAutoSave = true;
        try
        {
            _profiles.LoadAll(SelectedProfile);
        }
        finally
        {
            _suppressAutoSave = false;
        }
        RefreshProfiles();
        if (_binding.Current is not null && !string.IsNullOrWhiteSpace(SelectedProfile))
        {
            _profiles.RememberWindowProfile(_binding.Current, SelectedProfile);
            _registry.RefreshConfiguration(_binding.Current);
            ApplySelectedWindowAuxiliaryRuntimes();
        }
        AutoSaveStatusText = $"已切换方案 · {SelectedProfile}";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var window = _binding.Current;
        if (window is null)
        {
            _log.Warn(Module, "请先把绑定图标拖到要控制的游戏窗口。");
            SetStartFeedback("未启动", "请先选择一个游戏窗口。", true);
            UpdateRunAvailability();
            return;
        }

        if (!window.IsAutomationAllowed)
        {
            _log.Warn(Module, window.AutomationBlockReason);
            SetStartFeedback("未启动", window.AutomationBlockReason, true);
            UpdateRunAvailability();
            return;
        }

        var controllerKey = SelectedRunMode is null
            ? string.Empty
            : ControllerKeyForMode(SelectedRunMode.Key);
        var controller = controllerKey.Length == 0
            ? null
            : _registry.Find(window, controllerKey);
        if (controller is null)
        {
            _log.Warn(Module, "请选择可启动模式。");
            SetStartFeedback("未启动", "当前模式尚未接入运行能力。", true);
            return;
        }

        if (IsUnavailableBuiltInMonitorMode())
        {
            _log.Warn(Module, "启动已取消：内挂持续监控尚未接入。", window.Id);
            SetStartFeedback("未启动", "当前模式尚未就绪，请选择按键打怪。", true);
            UpdateRunAvailability();
            return;
        }

        StopCompetingMainModes(window, controller);
        controller = _registry.Prepare(window, controllerKey);
        if (controller is null)
        {
            _log.Error(Module, "启动失败：无法为当前窗口创建独立运行控制器。", window.Id);
            SetStartFeedback("启动失败", "当前窗口的运行实例未创建。", true);
            UpdateRunAvailability();
            return;
        }

        if (RequiresMainlineReadinessForSelectedMode())
        {
            var readiness = _mainlineReadiness.Inspect(_global.Current, window, includeReturnPointReadiness: true, includeLiveReturnRecognition: true);
            LogMainlineReadiness(readiness, window.Id);
            RefreshMainlineReadinessStatus();
            if (!readiness.CanStart)
            {
                _log.Error(Module, "启动已取消：关键键位或窗口状态未就绪。", window.Id);
                var reason = readiness.Items.FirstOrDefault(item => item.Severity == MainlineReadinessSeverity.Error)?.Message
                    ?? "关键键位或窗口状态未就绪。";
                SetStartFeedback("未启动", reason, true);
                UpdateRunAvailability();
                return;
            }
        }

        _log.Info(Module, $"启动模式：{controller.Name}，目标 PID {window.Pid}，窗口 {window.ClientSizeText}。", window.Id);
        controller.Start();
        SetStartFeedback(
            controller.RunState == RunState.Error ? "启动失败" : $"已启动：{controller.Name}",
            controller.RunState == RunState.Error ? "运行控制器进入错误状态，请查看运行记录。" : "任务已绑定到蓝色高亮窗口。",
            controller.RunState == RunState.Error);
        UpdateRunAvailability();
    }

    private void SetStartFeedback(string title, string summary, bool isError)
    {
        MainlineReadinessTitle = title;
        MainlineReadinessSummary = summary;
        MainlineReadinessHasErrors = isError;
    }

    private void LogMainlineReadiness(MainlineReadinessReport report, string windowId)
    {
        foreach (var item in report.Items)
        {
            var message = $"{item.Feature}：{item.Message}";
            switch (item.Severity)
            {
                case MainlineReadinessSeverity.Error:
                    _log.Error("启动检查", message, windowId);
                    break;
                case MainlineReadinessSeverity.Warning:
                    _log.Warn("启动检查", message, windowId);
                    break;
                default:
                    _log.Info("启动检查", message, windowId);
                    break;
            }
        }
    }

    private bool RequiresMainlineReadinessForSelectedMode() =>
        SelectedRunMode?.Key is FeatureKeys.HangNormal or FeatureKeys.HangBoss;

    private bool IsUnavailableBuiltInMonitorMode() =>
        RequiresMainlineReadinessForSelectedMode()
        && _global.Current.CombatBackendPreference == CombatBackendPreference.InternalPlugin;

    private bool CanPauseCurrent() =>
        SelectedController?.RunState is RunState.Running or RunState.Recovering or RunState.DeathReturn or RunState.Reconnecting or RunState.Switching;

    private bool CanStopCurrent() =>
        _binding.Current is { } window
        && _registry.FindForWindow(window).Any(controller => IsActiveState(controller.RunState));

    private bool CanStopAll() =>
        _registry.Controllers.Any(controller => IsActiveState(controller.RunState));

    [RelayCommand(CanExecute = nameof(CanPauseCurrent))]
    private void Pause()
    {
        SelectedController?.Pause();
        UpdateRunAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanStopCurrent))]
    private void Stop()
    {
        var window = _binding.Current;
        if (window is null)
            return;

        _registry.StopWindow(window);
        RunState = RunState.Stopped;
        _log.Info(Module, "已停止当前窗口的任务。", window.Id);
        UpdateRunAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanStopAll))]
    private void StopAll()
    {
        _registry.StopAll();

        RunState = RunState.Stopped;
        _log.Info(Module, "已停止全部模式。", _binding.Current?.Id ?? "-");
        UpdateRunAvailability();
    }
}
