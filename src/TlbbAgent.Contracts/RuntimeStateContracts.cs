namespace TlbbAgent.Contracts;

public enum RuntimeStateProviderKind
{
    Unknown = 0,
    ScreenshotOcr,
    StaticAsset,
    ReadOnlyProcess
}

public enum RuntimeSnapshotQuality
{
    Unavailable = 0,
    Partial,
    Reliable
}

public enum RuntimeProbeCapability
{
    ProcessMetadata = 0,
    VersionFingerprint,
    PlayerSnapshot,
    TargetSnapshot,
    PetSnapshot,
    SceneSnapshot,
    InventorySnapshot
}

public enum RuntimeProbeCapabilityStatus
{
    Unavailable = 0,
    Available,
    Planned,
    Blocked
}

public sealed record RuntimeProbeCapabilityResult(
    RuntimeProbeCapability Capability,
    RuntimeProbeCapabilityStatus Status,
    string Note);

public sealed record ReadOnlyProcessProbeResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "Unknown";
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public nint Hwnd { get; init; }
    public int Pid { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ProcessPath { get; init; } = string.Empty;
    public ClientBitness Bitness { get; init; } = ClientBitness.Unknown;
    public ClientType ClientType { get; init; } = ClientType.Unknown;
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public string MainModuleVersion { get; init; } = string.Empty;
    public string MainModuleSha256 { get; init; } = string.Empty;
    public string ProcessStartTime { get; init; } = string.Empty;
    public IReadOnlyList<RuntimeProbeCapabilityResult> Capabilities { get; init; } =
        Array.Empty<RuntimeProbeCapabilityResult>();
}

public sealed record PlayerRuntimeSnapshot
{
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public int? HpPercent { get; init; }
    public int? MpPercent { get; init; }
    public bool? IsDead { get; init; }
    public string? Name { get; init; }
    public string? School { get; init; }
}

public sealed record TargetRuntimeSnapshot
{
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public string? Name { get; init; }
    public int? HpPercent { get; init; }
    public bool? IsPlayer { get; init; }
    public bool? IsPet { get; init; }
    public bool? IsAttackable { get; init; }
    public bool? IsLost { get; init; }
    public string? Kind { get; init; }
}

public sealed record PetRuntimeSnapshot
{
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public int? HpPercent { get; init; }
    public bool? IsSummoned { get; init; }
}

public sealed record SceneRuntimeSnapshot
{
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public bool SceneUsable { get; init; }
    public bool MapFieldReady { get; init; }
    public bool CoordinateFieldReady { get; init; }
    public bool ReturnPointFieldReady { get; init; }
    public string GateReason { get; init; } = string.Empty;
    public string? MapName { get; init; }
    public string? MapNameSource { get; init; }
    public int MapNameConfidencePercent { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public string? CoordinateSource { get; init; }
    public int CoordinateConfidencePercent { get; init; }
    public bool? IsDungeon { get; init; }
}

public sealed record InventoryRuntimeSnapshot
{
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public bool? IsOpen { get; init; }
    public int? UsedSlots { get; init; }
    public int? EmptySlots { get; init; }
    public int? TotalSlots { get; init; }
    public int SellCandidateCount { get; init; }
    public int ProtectedCount { get; init; }
    public bool Executed { get; init; }
    public IReadOnlyList<InventoryCandidateRuntimeSnapshot> Candidates { get; init; } =
        Array.Empty<InventoryCandidateRuntimeSnapshot>();
}

public sealed record InventoryCandidateRuntimeSnapshot
{
    public int SlotIndex { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    public string? ItemName { get; init; }
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

public sealed record RuntimeSnapshot
{
    public RuntimeStateProviderKind Provider { get; init; } = RuntimeStateProviderKind.Unknown;
    public RuntimeSnapshotQuality Quality { get; init; } = RuntimeSnapshotQuality.Unavailable;
    public string Source { get; init; } = string.Empty;
    public int ConfidencePercent { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public string Message { get; init; } = string.Empty;
    public PlayerRuntimeSnapshot Player { get; init; } = new();
    public TargetRuntimeSnapshot Target { get; init; } = new();
    public PetRuntimeSnapshot Pet { get; init; } = new();
    public SceneRuntimeSnapshot Scene { get; init; } = new();
    public InventoryRuntimeSnapshot Inventory { get; init; } = new();
}

public interface IReadOnlyGameProcessProbe
{
    ReadOnlyProcessProbeResult Probe(BoundGameWindow window);
}

public interface IRuntimeStateProvider
{
    RuntimeStateProviderKind Kind { get; }
    RuntimeSnapshot Read(BoundGameWindow window);
}

public interface IRuntimeSnapshotService
{
    RuntimeSnapshot Read(BoundGameWindow window, RuntimeStateProviderKind preferredProvider);
    IReadOnlyList<RuntimeSnapshot> ReadAll(BoundGameWindow window);
}
