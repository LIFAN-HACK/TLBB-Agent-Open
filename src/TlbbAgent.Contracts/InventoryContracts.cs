namespace TlbbAgent.Contracts;

public enum InventoryScanMode
{
    ManualGrid = 0,
    AutoDetect
}

public enum InventorySlotState
{
    Unknown = 0,
    Empty,
    Occupied
}

public enum ItemCleanupDecision
{
    Keep = 0,
    Store,
    Trade,
    SellCandidate,
    BlockedUnknown
}

public enum BagCleanupExecutionMode
{
    Disabled = 0,
    DryRun,
    SellCandidates
}

public enum BagCleanupIntentMode
{
    MonthlyCard = 0,
    ReturnToCity
}

public enum BagCleanupReturnCity
{
    Luoyang = 0,
    Suzhou,
    Dali,
    Loulan
}

public sealed class InventoryGridConfig
{
    public InventoryScanMode ScanMode { get; set; } = InventoryScanMode.ManualGrid;
    public bool Enabled { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Columns { get; set; } = 8;
    public int Rows { get; set; } = 10;
    public int CellWidth { get; set; } = 40;
    public int CellHeight { get; set; } = 40;
    public int GapX { get; set; } = 3;
    public int GapY { get; set; } = 3;
    public Dictionary<int, string> SlotNameOverrides { get; set; } = new();
    public Dictionary<int, string> SlotNameSources { get; set; } = new();
}

public sealed class ItemPolicyConfig
{
    /// <summary>正式清包页面维护的精确出售许可名单；命中项仍只生成候选。</summary>
    public List<string> AllowedSellItems { get; set; } = new();
    public List<string> KeepItems { get; set; } = new();
    public List<string> StoreItems { get; set; } = new();
    public List<string> TradeItems { get; set; } = new();
    public List<string> SellKeywords { get; set; } = new();
    public List<string> BlockKeywords { get; set; } = new();
    public bool RequireExplicitSellKeyword { get; set; } = true;
    public bool ProtectUnknownItems { get; set; } = true;
}

public sealed class BagCleanupConfig
{
    public bool EnabledIntent { get; set; }
    public BagCleanupIntentMode IntentMode { get; set; } = BagCleanupIntentMode.MonthlyCard;
    public BagCleanupReturnCity ReturnCity { get; set; } = BagCleanupReturnCity.Luoyang;
    public BagCleanupExecutionMode Mode { get; set; } = BagCleanupExecutionMode.Disabled;
    public bool OpenBagBeforeScan { get; set; }
    /// <summary>仅作为用户显式配置后的降级入口；空值时不得猜测默认键位。</summary>
    public string OpenBagHotkey { get; set; } = "";
    public bool OpenBagHotkeyExplicitlyConfigured { get; set; }
    public bool AllowActualSell { get; set; }
    public int MinFreeSlotsWarning { get; set; } = 5;
    public InventoryGridConfig Grid { get; set; } = new();
    public ItemPolicyConfig Policy { get; set; } = new();
    public BagItemTemplateMatchConfig TemplateMatching { get; set; } = new();
}

public sealed class BagItemTemplateMatchConfig
{
    public bool Enabled { get; set; }
    public string IndexPath { get; set; } = "";
    public double Threshold { get; set; } = 0.92;
    public int InnerPadding { get; set; } = 1;
}

public sealed record InventorySlotSnapshot(
    int Index,
    int Row,
    int Column,
    int X,
    int Y,
    int Width,
    int Height,
    InventorySlotState State,
    string ItemName,
    double OccupancyScore,
    string Source);

public sealed record InventoryScanResult(
    bool Success,
    string Message,
    int Width,
    int Height,
    IReadOnlyList<InventorySlotSnapshot> Slots)
{
    public int OccupiedCount => Slots.Count(s => s.State == InventorySlotState.Occupied);
    public int EmptyCount => Slots.Count(s => s.State == InventorySlotState.Empty);
}

public sealed record InventoryGridCalibrationResult(
    bool Success,
    string Message,
    InventoryGridConfig? Grid,
    InventoryScanResult? PreviewScan);

public sealed record InventoryGridCandidate(
    InventoryGridConfig Grid,
    double Score,
    bool IsLikelyInventory,
    string Source);

public sealed record InventoryGridProbeResult(
    bool Success,
    string Message,
    IReadOnlyList<InventoryGridCandidate> Candidates);

public sealed record ItemCleanupCandidate(
    InventorySlotSnapshot Slot,
    ItemCleanupDecision Decision,
    string Reason);

public sealed record BagCleanupResult(
    bool Success,
    bool Executed,
    string Message,
    InventoryScanResult Scan,
    IReadOnlyList<ItemCleanupCandidate> Candidates)
{
    public int SellCandidateCount => Candidates.Count(c => c.Decision == ItemCleanupDecision.SellCandidate);
    public int ProtectedCount => Candidates.Count(c => c.Decision != ItemCleanupDecision.SellCandidate);
    public BagCleanupSafety Safety { get; init; } = BagCleanupSafety.ReadOnlyDryRun;
    public BagItemTemplateApplyResult TemplateMatch { get; init; } = BagItemTemplateApplyResult.Disabled;
}

public sealed record BagItemTemplateApplyResult(
    bool Enabled,
    bool Success,
    string Message,
    int TemplateCount,
    int OccupiedSlotCount,
    int MatchedCount,
    int AppliedCount,
    IReadOnlyList<BagItemTemplateSlotMatch> Slots)
{
    public static BagItemTemplateApplyResult Disabled { get; } = new(
        false,
        true,
        "模板匹配未启用。",
        0,
        0,
        0,
        0,
        Array.Empty<BagItemTemplateSlotMatch>());
}

public sealed record BagItemTemplateSlotMatch(
    int Index,
    int Row,
    int Column,
    bool Matched,
    bool AppliedToCleanup,
    double Score,
    string ItemName,
    string Policy,
    string TemplateId,
    string Reason);

public sealed record BagCleanupSafety(
    bool DryRun,
    bool SendsInput,
    bool ClicksMouse,
    bool SellsItems,
    bool ModifiesInventory,
    bool RequiresSecondConfirmation,
    bool UnknownItemsProtectedByDefault,
    string Mode)
{
    public static BagCleanupSafety ReadOnlyDryRun { get; } = new(
        true,
        false,
        false,
        false,
        false,
        true,
        true,
        "read-only-dry-run");

    public static BagCleanupSafety Disabled { get; } = new(
        true,
        false,
        false,
        false,
        false,
        true,
        true,
        "disabled");

    public static BagCleanupSafety BlockedActualSell(bool sendsInput = false) => new(
        false,
        sendsInput,
        false,
        false,
        false,
        true,
        true,
        "actual-sell-blocked");
}

public interface IInventoryRecognitionService
{
    InventoryGridCalibrationResult AutoCalibrate(BoundGameWindow window);
    InventoryGridCalibrationResult AutoCalibrate(CaptureResult capture);
    InventoryGridProbeResult ProbeGridCandidates(BoundGameWindow window, int maxCandidates);
    InventoryGridProbeResult ProbeGridCandidates(CaptureResult capture, int maxCandidates);
    InventoryScanResult Scan(BoundGameWindow window, InventoryGridConfig config);
    InventoryScanResult Scan(CaptureResult capture, InventoryGridConfig config);
}

public interface IItemPolicyService
{
    ItemCleanupDecision Decide(string itemName, ItemPolicyConfig config, out string reason);
    int LoadLegacyItemLists(string sourceRoot, ItemPolicyConfig config);
}

public interface IBagCleanupService
{
    BagCleanupResult Analyze(BoundGameWindow window, BagCleanupConfig config);
    BagCleanupResult Execute(BoundGameWindow window, BagCleanupConfig config);
}

public interface IBagItemTemplateMatchService
{
    BagItemTemplateApplyResult Apply(
        CaptureResult capture,
        InventoryGridConfig grid,
        ItemPolicyConfig policy,
        BagItemTemplateMatchConfig config);
}

public interface IBagCleanupReportService
{
    string BuildTextReport(BagCleanupResult result, BagCleanupConfig config);
    void SaveTextReport(string path, BagCleanupResult result, BagCleanupConfig config);
}
