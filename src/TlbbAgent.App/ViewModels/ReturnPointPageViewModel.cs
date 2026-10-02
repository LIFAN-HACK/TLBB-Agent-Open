using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class ReturnPointItemViewModel : ObservableObject
{
    private readonly ReturnPoint _model;
    private readonly Action _onChanged;

    public ReturnPointItemViewModel(ReturnPoint model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public ReturnPoint Model => _model;
    public string Id => _model.Id;
    public string MapName { get => _model.MapName; set { _model.MapName = value; OnPropertyChanged(); _onChanged(); } }
    public int X { get => _model.X; set { _model.X = value; OnPropertyChanged(); _onChanged(); } }
    public int Y { get => _model.Y; set { _model.Y = value; OnPropertyChanged(); _onChanged(); } }
    public string Label { get => _model.Label; set { _model.Label = value; OnPropertyChanged(); _onChanged(); } }
    public ReturnPointKind Kind { get => _model.Kind; set { _model.Kind = value; OnPropertyChanged(); _onChanged(); } }
    public string SourceModule => string.IsNullOrWhiteSpace(_model.SourceModule) ? "手动" : "配置";
}

public sealed partial class ReturnPointPageViewModel : PageViewModel
{
    private readonly IReturnPointService _service;
    private readonly IReturnPointCaptureService _capture;
    private readonly IReturnPointReadinessService _readiness;
    private readonly IReturnNavigationRecordImportService _recordImport;
    private readonly IRuntimeDataService _runtimeData;
    private readonly ILogService _log;
    private bool _refreshing;
    private bool _returnReadinessHasBeenChecked;
    private CancellationTokenSource? _returnReadinessRefreshCts;
    private CancellationTokenSource? _autoSaveCts;

    private static readonly HashSet<string> ConfigPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(EnableReturnPoint),
        nameof(EnableFixedFarmReturn),
        nameof(EnableEarthSpiritReturn),
        nameof(EnableDeathReturn),
        nameof(EnableStuckRecovery),
        nameof(StuckTimeoutMs),
        nameof(ReturnRetryLimit),
        nameof(CheckIntervalMs),
        nameof(ArrivalDistance),
        nameof(ReturnTravelWaitMs),
        nameof(ConfirmedMapName),
        nameof(MapRegionX),
        nameof(MapRegionY),
        nameof(MapRegionWidth),
        nameof(MapRegionHeight),
        nameof(CoordinateRegionX),
        nameof(CoordinateRegionY),
        nameof(CoordinateRegionWidth),
        nameof(CoordinateRegionHeight),
        nameof(DeathRegionX),
        nameof(DeathRegionY),
        nameof(DeathRegionWidth),
        nameof(DeathRegionHeight),
        nameof(DeathKeywords),
        nameof(ReturnNavigationMode),
        nameof(ReturnNavigationHotkey),
        nameof(ReturnNavigationDelayMs),
        nameof(ReturnNavigationClickX),
        nameof(ReturnNavigationClickY),
        nameof(ReturnNavigationActionIntervalMs),
        nameof(ReturnNavigationDispatchMode),
        nameof(EarthSpiritMode),
        nameof(EarthSpiritHotkey),
        nameof(EarthSpiritDelayMs),
        nameof(EarthSpiritClickX),
        nameof(EarthSpiritClickY),
        nameof(EarthSpiritActionIntervalMs),
        nameof(EarthSpiritDispatchMode),
        nameof(StuckRecoveryMode),
        nameof(StuckRecoveryHotkey),
        nameof(StuckRecoveryDelayMs),
        nameof(StuckRecoveryClickX),
        nameof(StuckRecoveryClickY),
        nameof(StuckRecoveryActionIntervalMs),
        nameof(StuckRecoveryDispatchMode),
        nameof(DeathReturnMode),
        nameof(DeathReturnHotkey),
        nameof(DeathReturnDelayMs),
        nameof(DeathReturnClickX),
        nameof(DeathReturnClickY),
        nameof(DeathReturnActionIntervalMs),
        nameof(DeathReturnDispatchMode)
    };

    public ReturnPointPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IReturnPointService service,
        IReturnPointCaptureService capture,
        IReturnPointReadinessService readiness,
        IReturnNavigationRecordImportService recordImport,
        IRuntimeDataService runtimeData,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _capture = capture;
        _readiness = readiness;
        _recordImport = recordImport;
        _runtimeData = runtimeData;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        PropertyChanged += (_, e) =>
        {
            if (_refreshing || string.IsNullOrWhiteSpace(e.PropertyName))
                return;

            if (ConfigPropertyNames.Contains(e.PropertyName))
            {
                _service.NotifyChanged();
                ScheduleReturnReadinessRefresh();
                ScheduleAutoSave();
            }
        };
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }

    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;
    public Array PointKinds { get; } = Enum.GetValues(typeof(ReturnPointKind));
    public Array ReturnNavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array ReturnNavigationSlots { get; } = Enum.GetValues(typeof(ReturnNavigationSlot));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));
    public ObservableCollection<ReturnPointItemViewModel> Points { get; } = new();
    public ObservableCollection<string> ConfirmedMapCandidates { get; } = new();

    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string PointSummary => $"地图 {_service.MapNames.Count} 个，坐标 {_service.Current.Points.Count} 个";
    public bool HasConfirmedMapCandidates => ConfirmedMapCandidates.Count > 0;
    public bool ConfirmedMapNameAutoFilled => _service.Current.NavigationRecognition.ConfirmedMapNameAutoFilled;
    public string ConfirmedMapStatus
    {
        get
        {
            var map = _service.Current.NavigationRecognition.ConfirmedMapName;
            if (string.IsNullOrWhiteSpace(map))
                return "未确认当前地图：地图 OCR 失败时不会自动回点。";

            var source = _service.Current.NavigationRecognition.ConfirmedMapNameAutoFilled ? "单地图自动确认" : "手动确认";
            return $"当前确认：{map}（{source}）。坐标稳定后允许匹配该地图返点。";
        }
    }

    public string ConfirmedMapCandidateSummary
    {
        get
        {
            var count = ConfirmedMapCandidates.Count;
            return count switch
            {
                0 => "未配置返点地图；可先录入或采集坐标。",
                1 => "当前配置只有 1 张地图，可一键确认。",
                _ => $"当前配置有 {count} 张地图，需要选择实际所在地图。"
            };
        }
    }

    public bool EnableFixedFarmReturn
    {
        get => _service.Current.EnableFixedFarmReturn;
        set { _service.Current.EnableFixedFarmReturn = value; OnPropertyChanged(); }
    }

    public bool EnableEarthSpiritReturn
    {
        get => _service.Current.EnableEarthSpiritReturn;
        set { _service.Current.EnableEarthSpiritReturn = value; OnPropertyChanged(); }
    }

    public bool EnableDeathReturn
    {
        get => _service.Current.EnableDeathReturn;
        set { _service.Current.EnableDeathReturn = value; OnPropertyChanged(); }
    }

    public bool EnableStuckRecovery
    {
        get => _service.Current.EnableStuckRecovery;
        set { _service.Current.EnableStuckRecovery = value; OnPropertyChanged(); }
    }

    public int StuckTimeoutMs
    {
        get => _service.Current.StuckTimeoutMs;
        set { _service.Current.StuckTimeoutMs = value; OnPropertyChanged(); }
    }

    public int ReturnRetryLimit
    {
        get => _service.Current.ReturnRetryLimit;
        set { _service.Current.ReturnRetryLimit = value; OnPropertyChanged(); }
    }

    public int CheckIntervalMs
    {
        get => _service.Current.CheckIntervalMs;
        set { _service.Current.CheckIntervalMs = value; OnPropertyChanged(); }
    }

    public int ArrivalDistance
    {
        get => _service.Current.ArrivalDistance;
        set { _service.Current.ArrivalDistance = value; OnPropertyChanged(); }
    }

    public bool EnableReturnPoint
    {
        get => _service.Current.EnableReturnPoint;
        set { _service.Current.EnableReturnPoint = value; OnPropertyChanged(); }
    }

    public int ReturnTravelWaitMs
    {
        get => _service.Current.ReturnTravelWaitMs;
        set { _service.Current.ReturnTravelWaitMs = value; OnPropertyChanged(); }
    }

    public int MapRegionX
    {
        get => _service.Current.NavigationRecognition.MapName.X;
        set { _service.Current.NavigationRecognition.MapName.X = value; OnPropertyChanged(); }
    }

    public string ConfirmedMapName
    {
        get => _service.Current.NavigationRecognition.ConfirmedMapName;
        set
        {
            _service.Current.NavigationRecognition.ConfirmedMapName = value;
            _service.Current.NavigationRecognition.ConfirmedMapNameAutoFilled = false;
            OnPropertyChanged();
            NotifyMapConfirmationProperties();
        }
    }

    public int MapRegionY
    {
        get => _service.Current.NavigationRecognition.MapName.Y;
        set { _service.Current.NavigationRecognition.MapName.Y = value; OnPropertyChanged(); }
    }

    public int MapRegionWidth
    {
        get => _service.Current.NavigationRecognition.MapName.Width;
        set { _service.Current.NavigationRecognition.MapName.Width = value; OnPropertyChanged(); }
    }

    public int MapRegionHeight
    {
        get => _service.Current.NavigationRecognition.MapName.Height;
        set { _service.Current.NavigationRecognition.MapName.Height = value; OnPropertyChanged(); }
    }

    public int CoordinateRegionX
    {
        get => _service.Current.NavigationRecognition.Coordinate.X;
        set { _service.Current.NavigationRecognition.Coordinate.X = value; OnPropertyChanged(); }
    }

    public int CoordinateRegionY
    {
        get => _service.Current.NavigationRecognition.Coordinate.Y;
        set { _service.Current.NavigationRecognition.Coordinate.Y = value; OnPropertyChanged(); }
    }

    public int CoordinateRegionWidth
    {
        get => _service.Current.NavigationRecognition.Coordinate.Width;
        set { _service.Current.NavigationRecognition.Coordinate.Width = value; OnPropertyChanged(); }
    }

    public int CoordinateRegionHeight
    {
        get => _service.Current.NavigationRecognition.Coordinate.Height;
        set { _service.Current.NavigationRecognition.Coordinate.Height = value; OnPropertyChanged(); }
    }

    public int DeathRegionX
    {
        get => _service.Current.DeathRecognition.PromptRegion.X;
        set { _service.Current.DeathRecognition.PromptRegion.X = value; OnPropertyChanged(); }
    }

    public int DeathRegionY
    {
        get => _service.Current.DeathRecognition.PromptRegion.Y;
        set { _service.Current.DeathRecognition.PromptRegion.Y = value; OnPropertyChanged(); }
    }

    public int DeathRegionWidth
    {
        get => _service.Current.DeathRecognition.PromptRegion.Width;
        set { _service.Current.DeathRecognition.PromptRegion.Width = value; OnPropertyChanged(); }
    }

    public int DeathRegionHeight
    {
        get => _service.Current.DeathRecognition.PromptRegion.Height;
        set { _service.Current.DeathRecognition.PromptRegion.Height = value; OnPropertyChanged(); }
    }

    public string DeathKeywords
    {
        get => _service.Current.DeathRecognition.Keywords;
        set { _service.Current.DeathRecognition.Keywords = value; OnPropertyChanged(); }
    }

    public ReturnNavigationMode ReturnNavigationMode
    {
        get => _service.Current.ReturnNavigation.Mode;
        set { _service.Current.ReturnNavigation.Mode = value; OnPropertyChanged(); }
    }

    public string ReturnNavigationHotkey
    {
        get => _service.Current.ReturnNavigation.Hotkey;
        set { _service.Current.ReturnNavigation.Hotkey = value; OnPropertyChanged(); }
    }

    public int ReturnNavigationDelayMs
    {
        get => _service.Current.ReturnNavigation.OpenMapDelayMs;
        set { _service.Current.ReturnNavigation.OpenMapDelayMs = value; OnPropertyChanged(); }
    }

    public int ReturnNavigationClickX
    {
        get => _service.Current.ReturnNavigation.ClickX;
        set { _service.Current.ReturnNavigation.ClickX = value; OnPropertyChanged(); }
    }

    public int ReturnNavigationClickY
    {
        get => _service.Current.ReturnNavigation.ClickY;
        set { _service.Current.ReturnNavigation.ClickY = value; OnPropertyChanged(); }
    }

    public int ReturnNavigationActionIntervalMs
    {
        get => _service.Current.ReturnNavigation.ActionIntervalMs;
        set { _service.Current.ReturnNavigation.ActionIntervalMs = value; OnPropertyChanged(); }
    }

    public InputDispatchMode ReturnNavigationDispatchMode
    {
        get => _service.Current.ReturnNavigation.DispatchMode;
        set { _service.Current.ReturnNavigation.DispatchMode = value; OnPropertyChanged(); }
    }

    public ReturnNavigationMode EarthSpiritMode
    {
        get => _service.Current.EarthSpiritNavigation.Mode;
        set { _service.Current.EarthSpiritNavigation.Mode = value; OnPropertyChanged(); }
    }

    public string EarthSpiritHotkey
    {
        get => _service.Current.EarthSpiritNavigation.Hotkey;
        set { _service.Current.EarthSpiritNavigation.Hotkey = value; OnPropertyChanged(); }
    }

    public int EarthSpiritDelayMs
    {
        get => _service.Current.EarthSpiritNavigation.OpenMapDelayMs;
        set { _service.Current.EarthSpiritNavigation.OpenMapDelayMs = value; OnPropertyChanged(); }
    }

    public int EarthSpiritClickX
    {
        get => _service.Current.EarthSpiritNavigation.ClickX;
        set { _service.Current.EarthSpiritNavigation.ClickX = value; OnPropertyChanged(); }
    }

    public int EarthSpiritClickY
    {
        get => _service.Current.EarthSpiritNavigation.ClickY;
        set { _service.Current.EarthSpiritNavigation.ClickY = value; OnPropertyChanged(); }
    }

    public int EarthSpiritActionIntervalMs
    {
        get => _service.Current.EarthSpiritNavigation.ActionIntervalMs;
        set { _service.Current.EarthSpiritNavigation.ActionIntervalMs = value; OnPropertyChanged(); }
    }

    public InputDispatchMode EarthSpiritDispatchMode
    {
        get => _service.Current.EarthSpiritNavigation.DispatchMode;
        set { _service.Current.EarthSpiritNavigation.DispatchMode = value; OnPropertyChanged(); }
    }

    public ReturnNavigationMode StuckRecoveryMode
    {
        get => _service.Current.StuckRecoveryNavigation.Mode;
        set { _service.Current.StuckRecoveryNavigation.Mode = value; OnPropertyChanged(); }
    }

    public string StuckRecoveryHotkey
    {
        get => _service.Current.StuckRecoveryNavigation.Hotkey;
        set { _service.Current.StuckRecoveryNavigation.Hotkey = value; OnPropertyChanged(); }
    }

    public int StuckRecoveryDelayMs
    {
        get => _service.Current.StuckRecoveryNavigation.OpenMapDelayMs;
        set { _service.Current.StuckRecoveryNavigation.OpenMapDelayMs = value; OnPropertyChanged(); }
    }

    public int StuckRecoveryClickX
    {
        get => _service.Current.StuckRecoveryNavigation.ClickX;
        set { _service.Current.StuckRecoveryNavigation.ClickX = value; OnPropertyChanged(); }
    }

    public int StuckRecoveryClickY
    {
        get => _service.Current.StuckRecoveryNavigation.ClickY;
        set { _service.Current.StuckRecoveryNavigation.ClickY = value; OnPropertyChanged(); }
    }

    public int StuckRecoveryActionIntervalMs
    {
        get => _service.Current.StuckRecoveryNavigation.ActionIntervalMs;
        set { _service.Current.StuckRecoveryNavigation.ActionIntervalMs = value; OnPropertyChanged(); }
    }

    public InputDispatchMode StuckRecoveryDispatchMode
    {
        get => _service.Current.StuckRecoveryNavigation.DispatchMode;
        set { _service.Current.StuckRecoveryNavigation.DispatchMode = value; OnPropertyChanged(); }
    }

    public ReturnNavigationMode DeathReturnMode
    {
        get => _service.Current.DeathReturnNavigation.Mode;
        set { _service.Current.DeathReturnNavigation.Mode = value; OnPropertyChanged(); }
    }

    public string DeathReturnHotkey
    {
        get => _service.Current.DeathReturnNavigation.Hotkey;
        set { _service.Current.DeathReturnNavigation.Hotkey = value; OnPropertyChanged(); }
    }

    public int DeathReturnDelayMs
    {
        get => _service.Current.DeathReturnNavigation.OpenMapDelayMs;
        set { _service.Current.DeathReturnNavigation.OpenMapDelayMs = value; OnPropertyChanged(); }
    }

    public int DeathReturnClickX
    {
        get => _service.Current.DeathReturnNavigation.ClickX;
        set { _service.Current.DeathReturnNavigation.ClickX = value; OnPropertyChanged(); }
    }

    public int DeathReturnClickY
    {
        get => _service.Current.DeathReturnNavigation.ClickY;
        set { _service.Current.DeathReturnNavigation.ClickY = value; OnPropertyChanged(); }
    }

    public int DeathReturnActionIntervalMs
    {
        get => _service.Current.DeathReturnNavigation.ActionIntervalMs;
        set { _service.Current.DeathReturnNavigation.ActionIntervalMs = value; OnPropertyChanged(); }
    }

    public InputDispatchMode DeathReturnDispatchMode
    {
        get => _service.Current.DeathReturnNavigation.DispatchMode;
        set { _service.Current.DeathReturnNavigation.DispatchMode = value; OnPropertyChanged(); }
    }

    [ObservableProperty] private string _newMapName = "嵩山";
    [ObservableProperty] private int _newX = 100;
    [ObservableProperty] private int _newY = 100;
    [ObservableProperty] private string _newLabel = "手动点";
    [ObservableProperty] private ReturnPointKind _newKind = ReturnPointKind.FixedFarm;
    [ObservableProperty] private string _importStatus = "可载入坐标，也可手动添加。";
    [ObservableProperty] private string _captureStatus = "可从当前绑定窗口读取地图/坐标并录入。";
    [ObservableProperty] private string _previewStatus = "可先只读识别当前地图/坐标，不写入配置。";
    [ObservableProperty] private ReturnNavigationSlot _recordImportSlot = ReturnNavigationSlot.FixedFarm;
    [ObservableProperty] private bool _recordImportForceOverwrite;
    [ObservableProperty] private bool _recordImportConfirmMap = true;
    [ObservableProperty] private bool _recordImportAddReturnPoint = true;
    [ObservableProperty] private string _recordImportPreHotkeys = "M,Tab";
    [ObservableProperty] private int _recordImportPreHotkeyDelayMs = 500;
    [ObservableProperty] private int _recordNavigationTargetX = 136;
    [ObservableProperty] private int _recordNavigationTargetY = 224;
    [ObservableProperty] private int _recordNavigationDurationMs = 20000;
    [ObservableProperty] private string _recordImportPointLabel = "录制回点";
    [ObservableProperty] private string _recordImportStatus = "导入采集器生成的 JSON，不会发送输入。";

    [ObservableProperty] private string _returnReadinessStatus = "未检查。点击“刷新门禁”查看当前方案是否能执行回点。";
    [ObservableProperty] private string _returnReadinessNextAction = "下一步：-";
    [ObservableProperty] private string _returnReadinessScene = "场景：-";
    [ObservableProperty] private string _returnReadinessSlots = "槽位：-";

    public string RecordNavigationCommandText
    {
        get
        {
            var duration = Math.Clamp(RecordNavigationDurationMs, 1000, 120000);
            var targetX = Math.Max(0, RecordNavigationTargetX);
            var targetY = Math.Max(0, RecordNavigationTargetY);
            var prepareDelay = Math.Clamp(RecordImportPreHotkeyDelayMs, 20, 5000);
            var prepareHotkeys = SanitizeCommandValue(RecordImportPreHotkeys);
            const int prepareYieldMs = 1200;

            return $"{DotnetDiagnosticsPrefix()} live-navigation-record{BuildMapOption()} --duration-ms {duration} --target {targetX},{targetY} --prepare-hotkeys {QuotePowerShellArg(prepareHotkeys)} --execute-prepare --prepare-delay-ms {prepareDelay} --yield-ms {prepareYieldMs}";
        }
    }

    public string RecordNavigationWorkflowText
    {
        get
        {
            var profile = string.IsNullOrWhiteSpace(_service.Current.ProfileName)
                ? "default"
                : _service.Current.ProfileName;
            var slot = SlotArgument(RecordImportSlot);
            var mapOption = BuildMapOption();
            var prepareDelay = Math.Clamp(RecordImportPreHotkeyDelayMs, 20, 5000);
            var prepareHotkeys = QuotePowerShellArg(SanitizeCommandValue(RecordImportPreHotkeys));
            var label = QuotePowerShellArg(string.IsNullOrWhiteSpace(RecordImportPointLabel) ? "录制回点" : RecordImportPointLabel);
            var force = RecordImportForceOverwrite ? " --force" : "";
            var addPoint = RecordImportAddReturnPoint ? " --add-point" : "";
            var confirmMap = RecordImportConfirmMap ? " --confirm-map" : "";
            var profileArg = QuotePowerShellArg(profile);
            var commandPrefix = DotnetDiagnosticsPrefix();

            return string.Join(Environment.NewLine, new[]
            {
                "# 0. 只读预览当前地图/坐标：不发输入，不写配置。",
                $"{commandPrefix} return-capture-current --profile {profileArg} --kind {slot} --label {label}",
                "",
                "# 0b. 预览正确后再执行带 --commit 的命令，把当前坐标写入当前方案。",
                $"{commandPrefix} return-capture-current --profile {profileArg} --kind {slot} --label {label} --commit",
                "",
                "# 1. 录制真实寻路样本：执行后在游戏地图上点击一次目标位置，必须看到 PASS。",
                RecordNavigationCommandText,
                "",
                "# 2. 只读校验最新录制样本。",
                $"{commandPrefix} return-navigation-record-verify --latest",
                "",
                "# 3. 导入到当前方案/槽位，覆盖开关由界面控制。",
                $"{commandPrefix} return-navigation-import-record --profile {profileArg}{mapOption} --slot {slot} --latest --pre-hotkeys {prepareHotkeys} --pre-delay-ms {prepareDelay}{force}{addPoint}{confirmMap} --label {label} --dispatch SendInput",
                "",
                "# 4. 真实窗口只读就绪检查，不发送输入。",
                $"{commandPrefix} live-return-navigation-readiness --profile {profileArg} --slot {slot}",
                "",
                "# 5. 受控预演：只写计划和门禁证据，不发送输入。",
                $"{commandPrefix} live-return-point-execute --profile {profileArg} --verify-ms 10000",
                "",
                "# 6. 只有第 4/5 步 PASS 后才执行真实回点。",
                $"{commandPrefix} live-return-point-execute --profile {profileArg} --execute --verify-ms 10000"
            });
        }
    }

    public string RecordedNavigationSummary
    {
        get
        {
            var navigation = NavigationConfigForSlot(RecordImportSlot);
            var clickCount = navigation.RecordedClicks.Count;
            var preHotkeyCount = navigation.RecordedPreHotkeys.Count;
            return clickCount == 0
                ? "当前槽位尚未导入录制序列。"
                : $"当前槽位已有录制序列：前置热键 {preHotkeyCount} 步，点击 {clickCount} 步。";
        }
    }

    public string RecordedNavigationGateStatus
    {
        get
        {
            var navigation = NavigationConfigForSlot(RecordImportSlot);
            if (navigation.RecordedClicks.Count == 0)
                return "未导入录制样本；不会执行录制回点。";

            var problems = new List<string>();
            if (!navigation.RecordedMovedCloserToTarget && !navigation.RecordedArrivedAtTarget)
                problems.Add("缺少靠近/到达验证");
            if (string.IsNullOrWhiteSpace(navigation.RecordedSourceClientSize))
                problems.Add("缺少来源窗口尺寸");
            if ((navigation.RecordedAfterX is null && navigation.RecordedTargetX is null)
                || (navigation.RecordedAfterY is null && navigation.RecordedTargetY is null))
                problems.Add("缺少终点/目标坐标");

            if (problems.Count > 0)
                return $"不可执行：{string.Join("；", problems)}。";

            var verified = navigation.RecordedArrivedAtTarget ? "已到达" : "已靠近";
            return $"可执行样本：{verified}；来源尺寸 {navigation.RecordedSourceClientSize}。";
        }
    }

    public string RecordedNavigationDestinationSummary
    {
        get
        {
            var navigation = NavigationConfigForSlot(RecordImportSlot);
            if (navigation.RecordedClicks.Count == 0)
                return "终点：-";

            var before = FormatRecordedPoint(navigation.RecordedBeforeMap, navigation.RecordedBeforeX, navigation.RecordedBeforeY);
            var after = FormatRecordedPoint(navigation.RecordedAfterMap, navigation.RecordedAfterX, navigation.RecordedAfterY);
            var target = FormatRecordedPoint("", navigation.RecordedTargetX, navigation.RecordedTargetY);
            return $"路径：{before} -> {after}；目标 {target}";
        }
    }

    public string RecordedNavigationEvidenceSummary
    {
        get
        {
            var navigation = NavigationConfigForSlot(RecordImportSlot);
            if (navigation.RecordedClicks.Count == 0)
                return "来源证据：-";

            var source = string.IsNullOrWhiteSpace(navigation.RecordedSourceEvidencePath)
                ? navigation.RecordedClicks.Select(c => c.SourceEvidencePath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? ""
                : navigation.RecordedSourceEvidencePath;
            return string.IsNullOrWhiteSpace(source)
                ? "来源证据：未记录"
                : $"来源证据：{Path.GetFileName(source)}";
        }
    }

    partial void OnRecordImportSlotChanged(ReturnNavigationSlot value)
    {
        OnPropertyChanged(nameof(RecordedNavigationSummary));
        OnPropertyChanged(nameof(RecordedNavigationGateStatus));
        OnPropertyChanged(nameof(RecordedNavigationDestinationSummary));
        OnPropertyChanged(nameof(RecordedNavigationEvidenceSummary));
        NotifyRecordNavigationProperties();
    }

    partial void OnRecordImportPreHotkeysChanged(string value) => NotifyRecordNavigationProperties();
    partial void OnRecordImportPreHotkeyDelayMsChanged(int value) => NotifyRecordNavigationProperties();
    partial void OnRecordNavigationTargetXChanged(int value) => NotifyRecordNavigationProperties();
    partial void OnRecordNavigationTargetYChanged(int value) => NotifyRecordNavigationProperties();
    partial void OnRecordNavigationDurationMsChanged(int value) => NotifyRecordNavigationProperties();
    partial void OnRecordImportForceOverwriteChanged(bool value) => NotifyRecordNavigationProperties();
    partial void OnRecordImportConfirmMapChanged(bool value) => NotifyRecordNavigationProperties();
    partial void OnRecordImportAddReturnPointChanged(bool value) => NotifyRecordNavigationProperties();
    partial void OnRecordImportPointLabelChanged(string value) => NotifyRecordNavigationProperties();

    private void NotifyRecordNavigationProperties()
    {
        OnPropertyChanged(nameof(RecordNavigationCommandText));
        OnPropertyChanged(nameof(RecordNavigationWorkflowText));
    }

    [RelayCommand]
    private void ConfirmMap(string? mapName)
    {
        var normalized = NormalizeMapName(mapName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        _service.Current.NavigationRecognition.ConfirmedMapName = normalized;
        _service.Current.NavigationRecognition.ConfirmedMapNameAutoFilled = false;
        NotifyMapConfirmationProperties();
        _service.NotifyChanged();
        SaveCurrentProfile();
        _log.Info("ReturnPoint", $"已手动确认当前地图：{normalized}");
    }

    [RelayCommand]
    private void UseSingleConfiguredMap()
    {
        var maps = _service.MapNames
            .Select(NormalizeMapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (maps.Length != 1)
        {
            _log.Warn("ReturnPoint", $"当前配置地图数量为 {maps.Length}，不能自动确认。");
            return;
        }

        ConfirmMap(maps[0]);
    }

    [RelayCommand]
    private void ClearConfirmedMap()
    {
        _service.Current.NavigationRecognition.ConfirmedMapName = "";
        _service.Current.NavigationRecognition.ConfirmedMapNameAutoFilled = false;
        NotifyMapConfirmationProperties();
        _service.NotifyChanged();
        SaveCurrentProfile();
        _log.Info("ReturnPoint", "已清空当前地图确认。");
    }

    [RelayCommand]
    private void AddPoint()
    {
        _service.AddPoint(new ReturnPoint
        {
            MapName = NewMapName.Trim(),
            X = NewX,
            Y = NewY,
            Label = NewLabel.Trim(),
            Kind = NewKind,
            SourceModule = "手动",
            SourcePath = ""
        });
        SaveCurrentProfile();
        ScheduleReturnReadinessRefresh();
    }

    private void NotifyMapConfirmationProperties()
    {
        OnPropertyChanged(nameof(ConfirmedMapName));
        OnPropertyChanged(nameof(ConfirmedMapNameAutoFilled));
        OnPropertyChanged(nameof(ConfirmedMapStatus));
        OnPropertyChanged(nameof(ConfirmedMapCandidateSummary));
        OnPropertyChanged(nameof(HasConfirmedMapCandidates));
        NotifyRecordNavigationProperties();
    }

    private static string NormalizeMapName(string mapName)
    {
        var chars = mapName.Trim()
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c))
            .ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    [RelayCommand]
    private void PreviewCurrentPoint()
    {
        var result = _capture.PreviewCurrentPoint();
        PreviewStatus = result.Message;
        if (!result.Success || result.Point is null)
            return;

        NewMapName = result.Point.MapName;
        NewX = result.Point.X;
        NewY = result.Point.Y;
        NewLabel = result.Point.Label;
        RefreshReturnReadiness(logResult: false);
    }

    [RelayCommand]
    private void CaptureCurrentPoint()
    {
        var result = _capture.CaptureAndAddCurrentPoint(NewKind, NewLabel);
        CaptureStatus = result.Message;
        if (!result.Success || result.Point is null)
            return;

        NewMapName = result.Point.MapName;
        NewX = result.Point.X;
        NewY = result.Point.Y;
        NewLabel = result.Point.Label;
        SaveCurrentProfile();
        RefreshReturnReadiness(logResult: false);
    }

    [RelayCommand]
    private void RemovePoint(ReturnPointItemViewModel? item)
    {
        if (item is null) return;
        _service.RemovePoint(item.Id);
        SaveCurrentProfile();
        ScheduleReturnReadinessRefresh();
    }

    [RelayCommand]
    private void ImportLegacyHorseRoutes()
    {
        if (!_runtimeData.TryResolveFile(Path.Combine("routes", "route-points-a.ini"), out var path))
        {
            ImportStatus = "未找到坐标文件。";
            _log.Warn("ReturnPoint", ImportStatus);
            return;
        }

        var result = _service.ImportLegacyIni(path, "配置");
        SaveCurrentProfile();
        RefreshReturnReadiness(logResult: false);
        ImportStatus = $"已载入：地图 {result.MapCount}，坐标 {result.PointCount}，跳过 {result.SkippedCount}。";
    }

    [RelayCommand]
    private void ImportRecordedNavigation()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入录制寻路样本",
            Filter = "导航记录 JSON|navigation-record-*.json;*.json|JSON 文件|*.json|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        var result = _recordImport.Import(new ReturnNavigationRecordImportRequest
        {
            ProfileName = _service.Current.ProfileName,
            EvidencePath = dialog.FileName,
            Slot = RecordImportSlot,
            Force = RecordImportForceOverwrite,
            ConfirmMapFromRecordEnd = RecordImportConfirmMap,
            AddReturnPointFromRecordEnd = RecordImportAddReturnPoint,
            ReturnPointLabel = RecordImportPointLabel,
            ReturnPointKind = ReturnPointKindForSlot(RecordImportSlot),
            MaxClicks = 8,
            FallbackDelayMs = 120,
            PreHotkeys = BuildRecordImportPreHotkeys(),
            DispatchMode = InputDispatchMode.PostMessage
        });

        RecordImportStatus = result.Success
            ? BuildRecordImportSuccessMessage(result)
            : $"导入失败：{result.Message}";

        if (result.Success)
        {
            ImportStatus = RecordImportStatus;
            _log.Info("ReturnPoint", RecordImportStatus);
            Refresh();
            SaveCurrentProfile();
            RefreshReturnReadiness(logResult: false);
        }
        else
        {
            _log.Warn("ReturnPoint", RecordImportStatus);
        }
    }

    [RelayCommand]
    private void CopyRecordNavigationCommand()
    {
        Clipboard.SetText(RecordNavigationCommandText);
        RecordImportStatus = "录制命令已复制；执行后必须显示 PASS 才能导入。";
        _log.Info("ReturnPoint", "录制寻路命令已复制。");
    }

    [RelayCommand]
    private void CopyRecordNavigationWorkflow()
    {
        Clipboard.SetText(RecordNavigationWorkflowText);
        RecordImportStatus = "完整回点录制流程已复制；按 1-6 步执行，真实执行前必须先通过只读就绪检查。";
        _log.Info("ReturnPoint", "完整回点录制流程已复制。");
    }

    [RelayCommand]
    private void RefreshReturnReadiness()
    {
        var report = _readiness.Inspect(includeLiveRecognition: true);
        _returnReadinessHasBeenChecked = true;
        ReturnReadinessStatus = $"{ReadinessLevelText(report.Level)}：{report.Summary}";
        ReturnReadinessNextAction = report.NextAction;
        ReturnReadinessScene = $"窗口：{report.WindowSummary}；场景：{report.SceneSummary}";
        ReturnReadinessSlots = string.Join(Environment.NewLine, report.Slots.Select(FormatSlotReadiness));

        var level = report.Level == ReturnPointReadinessLevel.Ready ? LogLevel.Info
            : report.Level == ReturnPointReadinessLevel.Warning ? LogLevel.Warn
            : LogLevel.Warn;
        _log.Log(level, "ReturnPoint", ReturnReadinessStatus);
    }

    private void RefreshReturnReadiness(bool logResult)
    {
        var report = _readiness.Inspect(includeLiveRecognition: true);
        _returnReadinessHasBeenChecked = true;
        ReturnReadinessStatus = $"{ReadinessLevelText(report.Level)}：{report.Summary}";
        ReturnReadinessNextAction = report.NextAction;
        ReturnReadinessScene = $"窗口：{report.WindowSummary}；场景：{report.SceneSummary}";
        ReturnReadinessSlots = string.Join(Environment.NewLine, report.Slots.Select(FormatSlotReadiness));

        if (!logResult)
            return;

        var level = report.Level == ReturnPointReadinessLevel.Ready ? LogLevel.Info
            : report.Level == ReturnPointReadinessLevel.Warning ? LogLevel.Warn
            : LogLevel.Warn;
        _log.Log(level, "ReturnPoint", ReturnReadinessStatus);
    }

    private void ScheduleReturnReadinessRefresh()
    {
        if (!_returnReadinessHasBeenChecked)
            return;

        _returnReadinessRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _returnReadinessRefreshCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, cts.Token);
                UiThread.Dispatch(() =>
                {
                    if (!cts.IsCancellationRequested)
                        RefreshReturnReadiness(logResult: false);
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, cts.Token);
                UiThread.Dispatch(() =>
                {
                    if (!cts.IsCancellationRequested)
                        SaveCurrentProfile();
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void SaveCurrentProfile()
    {
        var profileName = _service.Current.ProfileName;
        if (string.IsNullOrWhiteSpace(profileName))
            return;

        _service.Save(profileName);
    }

    private void OnPointItemChanged()
    {
        _service.NotifyChanged();
        ScheduleReturnReadinessRefresh();
        ScheduleAutoSave();
    }

    [RelayCommand]
    private void ClearPoints()
    {
        _service.Clear();
        SaveCurrentProfile();
        ScheduleReturnReadinessRefresh();
    }

    [RelayCommand]
    private void Save() => _service.Save(_service.Current.ProfileName);

    [RelayCommand]
    private void Load()
    {
        _service.Load(_service.Current.ProfileName);
        RefreshReturnReadiness(logResult: false);
    }

    private static string DotnetDiagnosticsPrefix() =>
        "& \"$env:ProgramFiles\\dotnet\\dotnet.exe\" run --no-build --project .\\tools\\TlbbAgent.Diagnostics\\TlbbAgent.Diagnostics.csproj --";

    private static string QuotePowerShellArg(string value) =>
        $"'{(value ?? string.Empty).Replace("'", "''")}'";

    private string BuildMapOption()
    {
        var mapName = NormalizeMapName(_service.Current.NavigationRecognition.ConfirmedMapName);
        return string.IsNullOrWhiteSpace(mapName) ? "" : $" --map {QuotePowerShellArg(mapName)}";
    }

    private static string SlotArgument(ReturnNavigationSlot slot) => slot switch
    {
        ReturnNavigationSlot.FixedFarm => "fixed",
        ReturnNavigationSlot.EarthSpirit => "earth",
        ReturnNavigationSlot.StuckRecovery => "stuck",
        ReturnNavigationSlot.DeathReturn => "death",
        _ => "fixed"
    };

    private static string ReadinessLevelText(ReturnPointReadinessLevel level) => level switch
    {
        ReturnPointReadinessLevel.Ready => "通过",
        ReturnPointReadinessLevel.Warning => "提示",
        _ => "阻断"
    };

    private static string FormatSlotReadiness(ReturnPointSlotReadiness slot)
    {
        if (!slot.Enabled)
            return $"{slot.SlotName}：未开启";

        var text = $"{slot.SlotName}：{ReadinessLevelText(slot.Level)} - {slot.Summary}";
        if (slot.Reasons.Count == 0)
            return text;

        return text + $"（{string.Join("；", slot.Reasons.Take(3))}）";
    }

    private static ReturnPointKind ReturnPointKindForSlot(ReturnNavigationSlot slot) => slot switch
    {
        ReturnNavigationSlot.FixedFarm => ReturnPointKind.FixedFarm,
        ReturnNavigationSlot.EarthSpirit => ReturnPointKind.EarthSpirit,
        ReturnNavigationSlot.StuckRecovery => ReturnPointKind.Recovery,
        ReturnNavigationSlot.DeathReturn => ReturnPointKind.DeathReturn,
        _ => ReturnPointKind.FixedFarm
    };

    private static string BuildRecordImportSuccessMessage(ReturnNavigationRecordImportResult result)
    {
        var preText = result.RecordedPreHotkeys.Count == 0 ? "" : $"；前置热键 {result.RecordedPreHotkeys.Count} 步";
        var mapText = result.ConfirmedMapFromRecordEnd ? "；已确认地图" : "";
        var pointText = result.AddedReturnPoint is null
            ? ""
            : $"；点位 {result.AddedReturnPoint.MapName} {result.AddedReturnPoint.X},{result.AddedReturnPoint.Y}";
        var verifyText = result.ArrivedAtTarget ? "；验证=已到达" : result.MovedCloserToTarget ? "；验证=已靠近" : "";
        var targetText = result.TargetX is null || result.TargetY is null ? "" : $"；目标 {result.TargetX},{result.TargetY}";
        return $"已导入：{result.SlotName}，点击 {result.RecordedClicks.Count} 步{preText}{targetText}{verifyText}{mapText}{pointText}。";
    }

    private IReadOnlyList<RecordedNavigationHotkey> BuildRecordImportPreHotkeys()
    {
        var delay = Math.Clamp(RecordImportPreHotkeyDelayMs, 20, 5000);
        return (RecordImportPreHotkeys ?? string.Empty)
            .Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => new RecordedNavigationHotkey
            {
                Hotkey = h,
                DelayAfterMs = delay
            })
            .ToArray();
    }

    private static string SanitizeCommandValue(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "M,Tab"
            : string.Join(",", value
                .Split(new[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(h => !string.IsNullOrWhiteSpace(h)));

        return string.IsNullOrWhiteSpace(normalized) ? "M,Tab" : normalized;
    }

    private static string FormatRecordedPoint(string mapName, int? x, int? y)
    {
        var coordinate = x is null || y is null ? "-,-" : $"{x},{y}";
        return string.IsNullOrWhiteSpace(mapName) ? coordinate : $"{mapName} {coordinate}";
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            Points.Clear();
            foreach (var point in _service.Current.Points)
                Points.Add(new ReturnPointItemViewModel(point, OnPointItemChanged));

            ConfirmedMapCandidates.Clear();
            foreach (var map in _service.MapNames.Select(NormalizeMapName).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal))
                ConfirmedMapCandidates.Add(map);

            OnPropertyChanged(nameof(PointSummary));
            NotifyMapConfirmationProperties();
            OnPropertyChanged(nameof(EnableReturnPoint));
            OnPropertyChanged(nameof(EnableFixedFarmReturn));
            OnPropertyChanged(nameof(EnableEarthSpiritReturn));
            OnPropertyChanged(nameof(EnableDeathReturn));
            OnPropertyChanged(nameof(EnableStuckRecovery));
            OnPropertyChanged(nameof(StuckTimeoutMs));
            OnPropertyChanged(nameof(ReturnRetryLimit));
            OnPropertyChanged(nameof(CheckIntervalMs));
            OnPropertyChanged(nameof(ArrivalDistance));
            OnPropertyChanged(nameof(ReturnTravelWaitMs));
            OnPropertyChanged(nameof(MapRegionX));
            OnPropertyChanged(nameof(MapRegionY));
            OnPropertyChanged(nameof(MapRegionWidth));
            OnPropertyChanged(nameof(MapRegionHeight));
            OnPropertyChanged(nameof(CoordinateRegionX));
            OnPropertyChanged(nameof(CoordinateRegionY));
            OnPropertyChanged(nameof(CoordinateRegionWidth));
            OnPropertyChanged(nameof(CoordinateRegionHeight));
            OnPropertyChanged(nameof(DeathRegionX));
            OnPropertyChanged(nameof(DeathRegionY));
            OnPropertyChanged(nameof(DeathRegionWidth));
            OnPropertyChanged(nameof(DeathRegionHeight));
            OnPropertyChanged(nameof(DeathKeywords));
            OnPropertyChanged(nameof(ReturnNavigationMode));
            OnPropertyChanged(nameof(ReturnNavigationHotkey));
            OnPropertyChanged(nameof(ReturnNavigationDelayMs));
            OnPropertyChanged(nameof(ReturnNavigationClickX));
            OnPropertyChanged(nameof(ReturnNavigationClickY));
            OnPropertyChanged(nameof(ReturnNavigationActionIntervalMs));
            OnPropertyChanged(nameof(ReturnNavigationDispatchMode));
            OnPropertyChanged(nameof(EarthSpiritMode));
            OnPropertyChanged(nameof(EarthSpiritHotkey));
            OnPropertyChanged(nameof(EarthSpiritDelayMs));
            OnPropertyChanged(nameof(EarthSpiritClickX));
            OnPropertyChanged(nameof(EarthSpiritClickY));
            OnPropertyChanged(nameof(EarthSpiritActionIntervalMs));
            OnPropertyChanged(nameof(EarthSpiritDispatchMode));
            OnPropertyChanged(nameof(StuckRecoveryMode));
            OnPropertyChanged(nameof(StuckRecoveryHotkey));
            OnPropertyChanged(nameof(StuckRecoveryDelayMs));
            OnPropertyChanged(nameof(StuckRecoveryClickX));
            OnPropertyChanged(nameof(StuckRecoveryClickY));
            OnPropertyChanged(nameof(StuckRecoveryActionIntervalMs));
            OnPropertyChanged(nameof(StuckRecoveryDispatchMode));
            OnPropertyChanged(nameof(DeathReturnMode));
            OnPropertyChanged(nameof(DeathReturnHotkey));
            OnPropertyChanged(nameof(DeathReturnDelayMs));
            OnPropertyChanged(nameof(DeathReturnClickX));
            OnPropertyChanged(nameof(DeathReturnClickY));
            OnPropertyChanged(nameof(DeathReturnActionIntervalMs));
            OnPropertyChanged(nameof(DeathReturnDispatchMode));
            OnPropertyChanged(nameof(RecordedNavigationSummary));
            OnPropertyChanged(nameof(RecordedNavigationGateStatus));
            OnPropertyChanged(nameof(RecordedNavigationDestinationSummary));
            OnPropertyChanged(nameof(RecordedNavigationEvidenceSummary));
            NotifyRecordNavigationProperties();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private ReturnNavigationConfig NavigationConfigForSlot(ReturnNavigationSlot slot) => slot switch
    {
        ReturnNavigationSlot.FixedFarm => _service.Current.ReturnNavigation,
        ReturnNavigationSlot.EarthSpirit => _service.Current.EarthSpiritNavigation,
        ReturnNavigationSlot.StuckRecovery => _service.Current.StuckRecoveryNavigation,
        ReturnNavigationSlot.DeathReturn => _service.Current.DeathReturnNavigation,
        _ => _service.Current.ReturnNavigation
    };

}
