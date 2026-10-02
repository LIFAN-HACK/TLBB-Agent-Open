namespace TlbbAgent.Contracts;

public enum ReturnPointKind
{
    Normal = 0,
    FixedFarm,
    EarthSpirit,
    DeathReturn,
    Recovery
}

public sealed class ReturnPoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string MapName { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public string Label { get; set; } = "";
    public ReturnPointKind Kind { get; set; } = ReturnPointKind.Normal;
    public string SourceModule { get; set; } = "";
    public string SourcePath { get; set; } = "";
}

public sealed class ReturnPointConfig
{
    public string ProfileName { get; set; } = "默认返点";
    public bool EnableReturnPoint { get; set; }
    public bool EnableFixedFarmReturn { get; set; }
    public bool EnableEarthSpiritReturn { get; set; }
    public bool EnableDeathReturn { get; set; }
    public bool EnableStuckRecovery { get; set; }
    public int CheckIntervalMs { get; set; } = 1000;
    public int ArrivalDistance { get; set; } = 8;
    public int StuckTimeoutMs { get; set; } = 15000;
    public int ReturnRetryLimit { get; set; } = 3;
    public int ReturnTravelWaitMs { get; set; } = 8000;
    public NavigationRecognitionConfig NavigationRecognition { get; set; } = new();
    public DeathRecognitionConfig DeathRecognition { get; set; } = new();
    public ReturnNavigationConfig ReturnNavigation { get; set; } = new();
    public ReturnNavigationConfig EarthSpiritNavigation { get; set; } = new();
    public ReturnNavigationConfig StuckRecoveryNavigation { get; set; } = new();
    public ReturnNavigationConfig DeathReturnNavigation { get; set; } = new();
    public List<ReturnPoint> Points { get; set; } = new();
}

public sealed class NavigationRecognitionConfig
{
    public string ConfirmedMapName { get; set; } = "";
    public bool ConfirmedMapNameAutoFilled { get; set; }
    public OcrRegionConfig MapName { get; set; } = new() { X = 2365, Y = 15, Width = 105, Height = 20 };
    public OcrRegionConfig Coordinate { get; set; } = new() { X = 2368, Y = 31, Width = 120, Height = 18 };
}

public sealed record NavigationRecognitionResult(
    string MapNameText,
    string CoordinateText,
    int? X,
    int? Y,
    string MapNameSource = "",
    int MapNameConfidencePercent = 0,
    string CoordinateSource = "",
    int CoordinateConfidencePercent = 0)
{
    public bool HasMap => HasMeaningfulMapName(MapNameText);
    public bool HasCoordinate => X is not null && Y is not null;

    private static bool HasMeaningfulMapName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var chars = text
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsDigit(c))
            .Where(c => c is not '一' and not '丨' and not '丶' and not '丿' and not '亅')
            .ToArray();

        return chars.Length >= 2;
    }
}

public sealed record MapNameSampleMatchResult(
    string MapName,
    double Score,
    bool IsConfident,
    string SampleId,
    string SamplePath);

public sealed class DeathRecognitionConfig
{
    public OcrRegionConfig PromptRegion { get; set; } = new() { X = 360, Y = 250, Width = 360, Height = 120 };
    public string Keywords { get; set; } = "死亡;复活;回城复活;返回城市;您已经死亡";
}

public sealed record DeathRecognitionResult(
    bool IsDead,
    string Text,
    string MatchedKeyword);

public enum ReturnNavigationMode
{
    Disabled = 0,
    ClickClientPoint,
    HotkeyOnly,
    HotkeyThenClickClientPoint,
    DirectionalClientClick,
    HotkeyThenDirectionalClientClick,
    RecordedClientClickSequence
}

public sealed class RecordedNavigationClick
{
    public int ClientX { get; set; }
    public int ClientY { get; set; }
    public InputMouseButton Button { get; set; } = InputMouseButton.Left;
    public int DelayAfterMs { get; set; } = 120;
    public string SourceEvidencePath { get; set; } = "";
}

public sealed class RecordedNavigationHotkey
{
    public string Hotkey { get; set; } = "";
    public int DelayAfterMs { get; set; } = 500;
    public string SourceEvidencePath { get; set; } = "";
}

public sealed class ReturnNavigationConfig
{
    public ReturnNavigationMode Mode { get; set; } = ReturnNavigationMode.Disabled;
    public string Hotkey { get; set; } = "";
    public int OpenMapDelayMs { get; set; } = 120;
    public int ClickX { get; set; } = 520;
    public int ClickY { get; set; } = 360;
    public InputMouseButton ClickButton { get; set; } = InputMouseButton.Left;
    public int ClickRepeat { get; set; } = 1;
    public int ClickIntervalMs { get; set; } = 120;
    public string AfterClickHotkey { get; set; } = "";
    public int AfterClickDelayMs { get; set; } = 120;
    public int DirectionRadius { get; set; } = 220;
    public bool DirectionInvertX { get; set; }
    public bool DirectionInvertY { get; set; }
    public int ActionIntervalMs { get; set; } = 2500;
    public InputDispatchMode DispatchMode { get; set; } = InputDispatchMode.Adaptive;
    public List<RecordedNavigationHotkey> RecordedPreHotkeys { get; set; } = new();
    public List<RecordedNavigationClick> RecordedClicks { get; set; } = new();
    public string RecordedSourceEvidencePath { get; set; } = "";
    public string RecordedSourceClientSize { get; set; } = "";
    public string RecordedBeforeMap { get; set; } = "";
    public int? RecordedBeforeX { get; set; }
    public int? RecordedBeforeY { get; set; }
    public string RecordedAfterMap { get; set; } = "";
    public int? RecordedAfterX { get; set; }
    public int? RecordedAfterY { get; set; }
    public int? RecordedTargetX { get; set; }
    public int? RecordedTargetY { get; set; }
    public bool RecordedMovedCloserToTarget { get; set; }
    public bool RecordedArrivedAtTarget { get; set; }
}

public enum ReturnNavigationSlot
{
    FixedFarm = 0,
    EarthSpirit,
    StuckRecovery,
    DeathReturn
}

public sealed class ReturnNavigationRecordImportRequest
{
    public string ProfileName { get; init; } = "";
    public string EvidencePath { get; init; } = "";
    public ReturnNavigationSlot Slot { get; init; } = ReturnNavigationSlot.FixedFarm;
    public bool Force { get; init; }
    public bool ConfirmMapFromRecordEnd { get; init; }
    public bool AddReturnPointFromRecordEnd { get; init; }
    public string ReturnPointLabel { get; init; } = "";
    public ReturnPointKind ReturnPointKind { get; init; } = ReturnPointKind.FixedFarm;
    public string ConfirmedMapName { get; init; } = "";
    public int MaxClicks { get; init; } = 8;
    public int FallbackDelayMs { get; init; } = 120;
    public IReadOnlyList<RecordedNavigationHotkey> PreHotkeys { get; init; } = Array.Empty<RecordedNavigationHotkey>();
    public InputDispatchMode DispatchMode { get; init; } = InputDispatchMode.Adaptive;
    public bool AllowSyntheticForDiagnostics { get; init; }
}

public sealed record ReturnNavigationRecordImportResult(
    bool Success,
    string Message,
    string ProfileName,
    bool ProfileExisted,
    ReturnNavigationSlot Slot,
    string SlotName,
    string EvidencePath,
    string SourceClientSize,
    string BeforeMap,
    string BeforeCoordinate,
    int? BeforeX,
    int? BeforeY,
    string AfterMap,
    string AfterCoordinate,
    int? AfterX,
    int? AfterY,
    int? TargetX,
    int? TargetY,
    bool MovedCloserToTarget,
    bool ArrivedAtTarget,
    bool ConfirmedMapFromRecordEnd,
    bool AddedReturnPointFromRecordEnd,
    ReturnPoint? AddedReturnPoint,
    IReadOnlyList<RecordedNavigationHotkey> RecordedPreHotkeys,
    IReadOnlyList<RecordedNavigationClick> RecordedClicks);

public sealed record ReturnNavigationResult(
    bool Executed,
    string Message);

public enum ReturnPointRuntimeAction
{
    None = 0,
    InRange,
    MissingNavigation,
    ReturnToPoint,
    EarthSpiritReturn,
    DeathReturn,
    StuckRecovery,
    Error
}

public sealed record ReturnPointRuntimeResult(
    ReturnPointRuntimeAction Action,
    bool ShouldSuspendCombat,
    string Message);

public sealed record ReturnPointImportResult(
    string SourcePath,
    int MapCount,
    int PointCount,
    int SkippedCount);

public sealed record ReturnPointCaptureResult(
    bool Success,
    string Message,
    ReturnPoint? Point,
    NavigationRecognitionResult? Navigation);

public interface IReturnPointService
{
    ReturnPointConfig Current { get; }
    event EventHandler? Changed;

    IReadOnlyList<string> MapNames { get; }
    void NotifyChanged();
    void AddPoint(ReturnPoint point);
    bool RemovePoint(string id);
    void Clear();
    ReturnPointImportResult ImportLegacyIni(string path, string sourceModule);
    void Save(string profileName);
    bool Load(string profileName);
}

public interface IReturnPointCaptureService
{
    ReturnPointCaptureResult PreviewCurrentPoint();
    ReturnPointCaptureResult CaptureAndAddCurrentPoint(ReturnPointKind kind, string label);
}

public interface IReturnPointRuntimeService
{
    bool HasEnabledReturnPoint { get; }
    void Reset();
    ReturnPointRuntimeResult Evaluate(BoundGameWindow window);
}

public enum ReturnPointReadinessLevel
{
    Ready = 0,
    Warning,
    Blocked
}

public sealed record ReturnPointSlotReadiness(
    ReturnNavigationSlot Slot,
    string SlotName,
    ReturnPointReadinessLevel Level,
    bool Enabled,
    string Summary,
    IReadOnlyList<string> Reasons);

public sealed record ReturnPointReadinessReport(
    ReturnPointReadinessLevel Level,
    string Summary,
    string NextAction,
    bool HasBoundWindow,
    string WindowSummary,
    string MapName,
    int? X,
    int? Y,
    string SceneSummary,
    IReadOnlyList<ReturnPointSlotReadiness> Slots);

public interface IReturnPointReadinessService
{
    ReturnPointReadinessReport Inspect(bool includeLiveRecognition = true);
}

public interface INavigationRecognitionService
{
    NavigationRecognitionResult Recognize(BoundGameWindow window, NavigationRecognitionConfig config);
}

public interface IMapNameSampleRecognitionService
{
    MapNameSampleMatchResult? Match(CaptureResult mapNameCapture);
}

public interface ISceneStateService
{
    NavigationRecognitionResult Recognize(BoundGameWindow window, NavigationRecognitionConfig config);
}

public interface IDeathRecognitionService
{
    DeathRecognitionResult Recognize(BoundGameWindow window, DeathRecognitionConfig config);
}

public interface IReturnNavigationService
{
    ReturnNavigationResult TryNavigate(
        BoundGameWindow window,
        NavigationRecognitionResult current,
        ReturnPoint targetPoint,
        double distance,
        ReturnNavigationConfig config);
}

public interface IReturnNavigationRecordImportService
{
    ReturnNavigationRecordImportResult Import(ReturnNavigationRecordImportRequest request);
}
