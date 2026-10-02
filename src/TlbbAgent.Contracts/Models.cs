namespace TlbbAgent.Contracts;

public enum ClientBitness
{
    Unknown = 0,
    X86,
    X64
}

public enum ClientType
{
    Unknown = 0,
    Tlbb32,
    Tlbb64
}

public enum BindingStatus
{
    Unbound = 0,
    Bound,
    Error
}

public enum RunState
{
    Idle = 0,
    Ready,
    Running,
    Paused,
    Recovering,
    DeathReturn,
    Reconnecting,
    Switching,
    Stopped,
    Error
}

public enum ModuleIntegrationStatus
{
    NotIntegrated = 0,
    Partial,
    Integrated,
    Running,
    Error
}

public enum LogLevel
{
    Trace = 0,
    Info,
    Warn,
    Error
}

public sealed class BoundGameWindow
{
    public nint Hwnd { get; init; }
    public int Pid { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public ClientBitness Bitness { get; init; } = ClientBitness.Unknown;
    public ClientType ClientType { get; init; } = ClientType.Unknown;
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public RunState RunState { get; set; } = RunState.Idle;

    public string Id => Hwnd == 0 ? "-" : $"0x{Hwnd.ToInt64():X}";
    public string ClientSizeText => ClientWidth > 0 && ClientHeight > 0 ? $"{ClientWidth}x{ClientHeight}" : "-";
    public bool IsStableClientArea => ClientWidth == 1024 && ClientHeight == 768;
    public bool IsAutomationAllowed => Bitness is ClientBitness.X86 or ClientBitness.X64;
    public bool IsResolutionCompatibilityMode => IsAutomationAllowed && !IsStableClientArea;

    public string ResolutionWarningReason => IsResolutionCompatibilityMode
        ? $"当前尺寸 {ClientSizeText}，建议校准。"
        : string.Empty;

    public string AutomationBlockReason
    {
        get
        {
            if (!IsAutomationAllowed)
                return "无法识别游戏窗口，已阻止启动。";
            return string.Empty;
        }
    }
}

public enum LogAudience
{
    Auto = 0,
    User,
    Diagnostic
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Window { get; init; } = "-";
    public string Module { get; init; } = "-";
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Message { get; init; } = string.Empty;
    public LogAudience Audience { get; init; } = LogAudience.Auto;
    public string? UserSummary { get; init; }
    public string? DebugDetails { get; init; }
    public string? RoleSessionId { get; init; }
    public string? FeatureKey { get; init; }
    public string? CommandId { get; init; }

    public override string ToString()
    {
        var safeMessage = Audience == LogAudience.User && !string.IsNullOrWhiteSpace(UserSummary)
            ? UserSummary
            : Audience == LogAudience.Diagnostic
                ? "[诊断事件已隐藏]"
                : "[运行事件]";
        return $"{Time:HH:mm:ss}  {Level,-5}  {safeMessage}";
    }
}

public sealed class CombatLoopTelemetryEntry
{
    public int SchemaVersion { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string Event { get; init; } = "";
    public string Controller { get; init; } = "";
    public string Window { get; init; } = "-";
    public int Pid { get; init; }
    public string ClientSize { get; init; } = "-";
    public string Mode { get; init; } = "";
    public string InputMode { get; init; } = "";
    public string Decision { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Actions { get; init; } = "";
    public string Context { get; init; } = "";
    public string AttackResult { get; init; } = "";
    public bool SkillSent { get; init; }
    public bool AttackSentForCurrentTarget { get; init; }
    public int AttackActionsOnCurrentTarget { get; init; }
    public int PlannedAttackNoActionScans { get; init; }
    public string TargetName { get; init; } = "";
    public int? TargetHp { get; init; }
    public int? PlayerHp { get; init; }
    public int? PlayerMp { get; init; }
    public int? PetHp { get; init; }
    public bool TargetLooksLikePlayer { get; init; }
    public bool TargetProtectionAmbiguous { get; init; }
    public bool TargetLooksLikePet { get; init; }
    public bool? TargetIsAttackable { get; init; }
    public bool TargetHasSchoolMarker { get; init; }
    public bool TargetHasPlayerPortrait { get; init; }
    public bool TargetHasPlayerStatusBars { get; init; }
    public string TargetSampleKind { get; init; } = "";
    public string TargetSampleLabel { get; init; } = "";
    public string TargetSampleSource { get; init; } = "";
    public double TargetSampleScore { get; init; }
}

public sealed class ModuleDescriptor
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Group { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceModule { get; init; } = string.Empty;
    public string Priority { get; init; } = "P0";
    public ModuleIntegrationStatus Status { get; set; } = ModuleIntegrationStatus.NotIntegrated;
}

public sealed class AppPaths
{
    public required string Root { get; init; }
    public required string Profiles { get; init; }
    public required string Logs { get; init; }
    public required string Data { get; init; }
    public required string Ocr { get; init; }
    public required string Backups { get; init; }
    public required string LegacyImports { get; init; }
}
