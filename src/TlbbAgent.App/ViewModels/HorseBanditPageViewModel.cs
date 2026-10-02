using System.IO;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class HorseBanditPageViewModel : PageViewModel
{
    private readonly IHorseBanditConfigService _service;
    private readonly IRuntimeDataService _runtimeData;
    private readonly ILogService _log;

    public HorseBanditPageViewModel(
        ModuleDescriptor descriptor,
        IAutomationController? controller,
        IHorseBanditConfigService service,
        IRuntimeDataService runtimeData,
        ILogService log)
    {
        Descriptor = descriptor;
        Controller = controller;
        _service = service;
        _runtimeData = runtimeData;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        _controllerSubscribe();
        Refresh();
    }

    public ModuleDescriptor Descriptor { get; }
    public IAutomationController? Controller { get; }
    public override string Title => Descriptor.Title;
    public override string Subtitle => Descriptor.Description;
    public Array Roles { get; } = Enum.GetValues(typeof(HorseBanditRole));
    public Array ReturnNavigationModes { get; } = Enum.GetValues(typeof(ReturnNavigationMode));
    public Array DispatchModes { get; } = Enum.GetValues(typeof(InputDispatchMode));

    public bool HasController => Controller is not null;
    public RunState RunState => Controller?.RunState ?? RunState.Idle;
    public string CurrentStateName => Controller?.CurrentStateName ?? "-";
    public string RouteSummary => $"地图 {_service.MapNames.Count} 个，坐标 {_service.Current.RoutePoints.Count} 个";
    public string RoleSummary => C.Role == HorseBanditRole.Leader ? "队长" : "队员";
    public string FollowSummary => C.EnableAutoFollow
        ? $"自动跟随，掉队 {C.LostFollowDistance}，恢复 {C.RecoverDelayMs}ms"
        : "不自动跟随";
    public string TeamSummary
    {
        get
        {
            var checks = new List<string>();
            if (C.EnableTeamCheck) checks.Add("队伍识别");
            if (C.AgreeTeamInvite) checks.Add("同意进队");
            if (C.CheckMembersBeforeStart) checks.Add("启动前");
            if (C.CheckMembersAfterStart) checks.Add("启动后");
            if (C.CheckMembersDuringPatrol) checks.Add("巡逻中");
            return checks.Count == 0 ? "未启用队伍检查" : string.Join(" / ", checks);
        }
    }
    public string TargetSummary
    {
        get
        {
            var count = SplitKeywords(C.TargetKeywords).Count;
            return count == 0 ? "未配置目标" : $"目标词 {count} 个";
        }
    }
    public string HotkeySummary
    {
        get
        {
            var search = string.IsNullOrWhiteSpace(C.SearchHotkey) ? "-" : C.SearchHotkey;
            var attack = string.IsNullOrWhiteSpace(C.AttackHotkey) ? "-" : C.AttackHotkey;
            var follow = string.IsNullOrWhiteSpace(C.FollowHotkey) ? "-" : C.FollowHotkey;
            return $"搜 {search} / 攻 {attack} / 跟 {follow}";
        }
    }
    public string PatrolSummary
    {
        get
        {
            var route = C.RoutePoints.Count == 0
                ? "无路线"
                : $"{_service.MapNames.Count} 图 / {C.RoutePoints.Count} 点";
            var nav = C.PatrolNavigation.Mode == ReturnNavigationMode.Disabled ? "巡逻未启用" : "巡逻已启用";
            return $"{route}，{nav}";
        }
    }
    public string ReadySummary
    {
        get
        {
            if (SplitKeywords(C.TargetKeywords).Count == 0)
                return "缺少目标词";
            if (string.IsNullOrWhiteSpace(C.SearchHotkey))
                return "缺少搜索键";
            if (C.Role == HorseBanditRole.Member && C.EnableAutoFollow && string.IsNullOrWhiteSpace(C.FollowHotkey))
                return "队员缺少跟随键";
            if ((C.UseBuiltInRoutes || C.UseCustomRoutes) && C.RoutePoints.Count == 0)
                return "缺少巡逻路线";
            return "可启动";
        }
    }
    public bool IsReady => ReadySummary == "可启动";

    public HorseBanditRole Role { get => C.Role; set { C.Role = value; OnConfigPropertyChanged(); } }
    public bool EnableAutoFollow { get => C.EnableAutoFollow; set { C.EnableAutoFollow = value; OnConfigPropertyChanged(); } }
    public bool EnableTeamCheck { get => C.EnableTeamCheck; set { C.EnableTeamCheck = value; OnConfigPropertyChanged(); } }
    public bool AgreeTeamInvite { get => C.AgreeTeamInvite; set { C.AgreeTeamInvite = value; OnConfigPropertyChanged(); } }
    public bool WaitMembers { get => C.WaitMembers; set { C.WaitMembers = value; OnPropertyChanged(); } }
    public int WaitMemberSeconds { get => C.WaitMemberSeconds; set { C.WaitMemberSeconds = value; OnPropertyChanged(); } }
    public bool MemberSelectsTarget { get => C.MemberSelectsTarget; set { C.MemberSelectsTarget = value; OnPropertyChanged(); } }
    public bool LeaderNoAttack { get => C.LeaderNoAttack; set { C.LeaderNoAttack = value; OnPropertyChanged(); } }
    public bool LeaderStayMounted { get => C.LeaderStayMounted; set { C.LeaderStayMounted = value; OnPropertyChanged(); } }
    public bool MemberAttackAllTargets { get => C.MemberAttackAllTargets; set { C.MemberAttackAllTargets = value; OnPropertyChanged(); } }
    public bool CheckMembersBeforeStart { get => C.CheckMembersBeforeStart; set { C.CheckMembersBeforeStart = value; OnConfigPropertyChanged(); } }
    public bool CheckMembersAfterStart { get => C.CheckMembersAfterStart; set { C.CheckMembersAfterStart = value; OnConfigPropertyChanged(); } }
    public bool CheckMembersDuringPatrol { get => C.CheckMembersDuringPatrol; set { C.CheckMembersDuringPatrol = value; OnConfigPropertyChanged(); } }
    public int LostFollowDistance { get => C.LostFollowDistance; set { C.LostFollowDistance = value; OnConfigPropertyChanged(); } }
    public int CirclePullFollowCount { get => C.CirclePullFollowCount; set { C.CirclePullFollowCount = value; OnPropertyChanged(); } }
    public int ForwardDistance { get => C.ForwardDistance; set { C.ForwardDistance = value; OnPropertyChanged(); } }
    public int LeadDistance { get => C.LeadDistance; set { C.LeadDistance = value; OnPropertyChanged(); } }
    public bool UseBuiltInRoutes { get => C.UseBuiltInRoutes; set { C.UseBuiltInRoutes = value; OnConfigPropertyChanged(); } }
    public bool UseCustomRoutes { get => C.UseCustomRoutes; set { C.UseCustomRoutes = value; OnConfigPropertyChanged(); } }
    public int WaitX { get => C.WaitX; set { C.WaitX = value; OnPropertyChanged(); } }
    public int WaitY { get => C.WaitY; set { C.WaitY = value; OnPropertyChanged(); } }
    public int FindHorseMode { get => C.FindHorseMode; set { C.FindHorseMode = value; OnPropertyChanged(); } }
    public string AvoidMaps { get => C.AvoidMaps; set { C.AvoidMaps = value; OnPropertyChanged(); } }
    public string TargetKeywords { get => C.TargetKeywords; set { C.TargetKeywords = value; OnConfigPropertyChanged(); } }
    public string SearchHotkey { get => C.SearchHotkey; set { C.SearchHotkey = value; OnConfigPropertyChanged(); } }
    public string AttackHotkey { get => C.AttackHotkey; set { C.AttackHotkey = value; OnConfigPropertyChanged(); } }
    public string MountHotkey { get => C.MountHotkey; set { C.MountHotkey = value; OnPropertyChanged(); } }
    public string FollowHotkey { get => C.FollowHotkey; set { C.FollowHotkey = value; OnConfigPropertyChanged(); } }
    public int ScanIntervalMs { get => C.ScanIntervalMs; set { C.ScanIntervalMs = value; OnPropertyChanged(); } }
    public int AttackIntervalMs { get => C.AttackIntervalMs; set { C.AttackIntervalMs = value; OnPropertyChanged(); } }
    public ReturnNavigationMode PatrolNavigationMode { get => C.PatrolNavigation.Mode; set { C.PatrolNavigation.Mode = value; OnConfigPropertyChanged(); } }
    public string PatrolNavigationHotkey { get => C.PatrolNavigation.Hotkey; set { C.PatrolNavigation.Hotkey = value; OnPropertyChanged(); } }
    public int PatrolOpenMapDelayMs { get => C.PatrolNavigation.OpenMapDelayMs; set { C.PatrolNavigation.OpenMapDelayMs = value; OnPropertyChanged(); } }
    public int PatrolClickX { get => C.PatrolNavigation.ClickX; set { C.PatrolNavigation.ClickX = value; OnPropertyChanged(); } }
    public int PatrolClickY { get => C.PatrolNavigation.ClickY; set { C.PatrolNavigation.ClickY = value; OnPropertyChanged(); } }
    public int PatrolActionIntervalMs { get => C.PatrolNavigation.ActionIntervalMs; set { C.PatrolNavigation.ActionIntervalMs = value; OnPropertyChanged(); } }
    public InputDispatchMode PatrolDispatchMode { get => C.PatrolNavigation.DispatchMode; set { C.PatrolNavigation.DispatchMode = value; OnPropertyChanged(); } }
    public int TeamRegionX { get => C.Recognition.TeamStatus.X; set { C.Recognition.TeamStatus.X = value; OnPropertyChanged(); } }
    public int TeamRegionY { get => C.Recognition.TeamStatus.Y; set { C.Recognition.TeamStatus.Y = value; OnPropertyChanged(); } }
    public int TeamRegionWidth { get => C.Recognition.TeamStatus.Width; set { C.Recognition.TeamStatus.Width = value; OnPropertyChanged(); } }
    public int TeamRegionHeight { get => C.Recognition.TeamStatus.Height; set { C.Recognition.TeamStatus.Height = value; OnPropertyChanged(); } }
    public int FollowRegionX { get => C.Recognition.FollowStatus.X; set { C.Recognition.FollowStatus.X = value; OnPropertyChanged(); } }
    public int FollowRegionY { get => C.Recognition.FollowStatus.Y; set { C.Recognition.FollowStatus.Y = value; OnPropertyChanged(); } }
    public int FollowRegionWidth { get => C.Recognition.FollowStatus.Width; set { C.Recognition.FollowStatus.Width = value; OnPropertyChanged(); } }
    public int FollowRegionHeight { get => C.Recognition.FollowStatus.Height; set { C.Recognition.FollowStatus.Height = value; OnPropertyChanged(); } }
    public int DistanceRegionX { get => C.Recognition.DistanceText.X; set { C.Recognition.DistanceText.X = value; OnPropertyChanged(); } }
    public int DistanceRegionY { get => C.Recognition.DistanceText.Y; set { C.Recognition.DistanceText.Y = value; OnPropertyChanged(); } }
    public int DistanceRegionWidth { get => C.Recognition.DistanceText.Width; set { C.Recognition.DistanceText.Width = value; OnPropertyChanged(); } }
    public int DistanceRegionHeight { get => C.Recognition.DistanceText.Height; set { C.Recognition.DistanceText.Height = value; OnPropertyChanged(); } }
    public string FollowOkKeywords { get => C.Recognition.FollowOkKeywords; set { C.Recognition.FollowOkKeywords = value; OnPropertyChanged(); } }
    public string LostFollowKeywords { get => C.Recognition.LostFollowKeywords; set { C.Recognition.LostFollowKeywords = value; OnPropertyChanged(); } }
    public string SourceSummary => string.IsNullOrWhiteSpace(C.SourceSummary) ? "未载入配置。" : "配置已载入。";

    [ObservableProperty] private string _importStatus = "可载入跟随、找马和路线。";

    private HorseBanditConfig C => _service.Current;

    [RelayCommand]
    private void ImportMemberHorseConfig() => ImportConfig("follow-a.sanitized.ini", "跟随配置");

    [RelayCommand]
    private void ImportHorse231Config() => ImportConfig("horse-a.sanitized.ini", "找马配置");

    [RelayCommand]
    private void ImportHorseRoutes()
    {
        if (!_runtimeData.TryResolveFile(Path.Combine("routes", "route-points-a.ini"), out var path))
        {
            ImportStatus = "未找到路线文件。";
            _log.Warn("HorseBandit", ImportStatus);
            return;
        }

        var result = _service.ImportLegacyRoutes(path, "配置");
        ImportStatus = $"已载入路线：地图 {result.RouteMapCount}，坐标 {result.RoutePointCount}，跳过 {result.SkippedCount}。";
    }

    [RelayCommand]
    private void Save() => _service.Save(_service.Current.ProfileName);

    [RelayCommand]
    private void Load() => _service.Load(_service.Current.ProfileName);

    private void ImportConfig(string relativeName, string sourceModule)
    {
        if (!_runtimeData.TryResolveFile(Path.Combine("config-samples", relativeName), out var path))
        {
            ImportStatus = "未找到配置文件。";
            _log.Warn("HorseBandit", ImportStatus);
            return;
        }

        var result = _service.ImportLegacyConfig(path, sourceModule);
        ImportStatus = $"已载入：{sourceModule}，字段 {result.FieldCount}。";
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(Role));
        OnPropertyChanged(nameof(EnableAutoFollow));
        OnPropertyChanged(nameof(EnableTeamCheck));
        OnPropertyChanged(nameof(AgreeTeamInvite));
        OnPropertyChanged(nameof(WaitMembers));
        OnPropertyChanged(nameof(WaitMemberSeconds));
        OnPropertyChanged(nameof(MemberSelectsTarget));
        OnPropertyChanged(nameof(LeaderNoAttack));
        OnPropertyChanged(nameof(LeaderStayMounted));
        OnPropertyChanged(nameof(MemberAttackAllTargets));
        OnPropertyChanged(nameof(CheckMembersBeforeStart));
        OnPropertyChanged(nameof(CheckMembersAfterStart));
        OnPropertyChanged(nameof(CheckMembersDuringPatrol));
        OnPropertyChanged(nameof(LostFollowDistance));
        OnPropertyChanged(nameof(CirclePullFollowCount));
        OnPropertyChanged(nameof(ForwardDistance));
        OnPropertyChanged(nameof(LeadDistance));
        OnPropertyChanged(nameof(UseBuiltInRoutes));
        OnPropertyChanged(nameof(UseCustomRoutes));
        OnPropertyChanged(nameof(WaitX));
        OnPropertyChanged(nameof(WaitY));
        OnPropertyChanged(nameof(FindHorseMode));
        OnPropertyChanged(nameof(AvoidMaps));
        OnPropertyChanged(nameof(TargetKeywords));
        OnPropertyChanged(nameof(SearchHotkey));
        OnPropertyChanged(nameof(AttackHotkey));
        OnPropertyChanged(nameof(MountHotkey));
        OnPropertyChanged(nameof(FollowHotkey));
        OnPropertyChanged(nameof(ScanIntervalMs));
        OnPropertyChanged(nameof(AttackIntervalMs));
        OnPropertyChanged(nameof(PatrolNavigationMode));
        OnPropertyChanged(nameof(PatrolNavigationHotkey));
        OnPropertyChanged(nameof(PatrolOpenMapDelayMs));
        OnPropertyChanged(nameof(PatrolClickX));
        OnPropertyChanged(nameof(PatrolClickY));
        OnPropertyChanged(nameof(PatrolActionIntervalMs));
        OnPropertyChanged(nameof(PatrolDispatchMode));
        OnPropertyChanged(nameof(TeamRegionX));
        OnPropertyChanged(nameof(TeamRegionY));
        OnPropertyChanged(nameof(TeamRegionWidth));
        OnPropertyChanged(nameof(TeamRegionHeight));
        OnPropertyChanged(nameof(FollowRegionX));
        OnPropertyChanged(nameof(FollowRegionY));
        OnPropertyChanged(nameof(FollowRegionWidth));
        OnPropertyChanged(nameof(FollowRegionHeight));
        OnPropertyChanged(nameof(DistanceRegionX));
        OnPropertyChanged(nameof(DistanceRegionY));
        OnPropertyChanged(nameof(DistanceRegionWidth));
        OnPropertyChanged(nameof(DistanceRegionHeight));
        OnPropertyChanged(nameof(FollowOkKeywords));
        OnPropertyChanged(nameof(LostFollowKeywords));
        OnPropertyChanged(nameof(SourceSummary));
        RefreshSummary();
    }

    private void _controllerSubscribe()
    {
        if (Controller is null)
            return;

        Controller.StateChanged += (_, _) => UiThread.Dispatch(() =>
        {
            OnPropertyChanged(nameof(CurrentStateName));
            OnPropertyChanged(nameof(RunState));
        });
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(RoleSummary));
        OnPropertyChanged(nameof(FollowSummary));
        OnPropertyChanged(nameof(TeamSummary));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(HotkeySummary));
        OnPropertyChanged(nameof(PatrolSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }

    private void OnConfigPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        RefreshSummary();
    }

    private static IReadOnlyList<string> SplitKeywords(string value) =>
        (value ?? string.Empty)
            .Split(new[] { ';', ',', '，', '|', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();

}

