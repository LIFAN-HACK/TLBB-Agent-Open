namespace TlbbAgent.Contracts;
public interface IAppInitService
{
    AppPaths Paths { get; }
    void Initialize();
}

public interface IRuntimeWarmupService
{
    Task WarmupAsync(CancellationToken cancellationToken = default);
}

public interface IWindowBindingService
{
    BoundGameWindow? Current { get; }
    event EventHandler<BoundGameWindow?>? CurrentChanged;
    BoundGameWindow? BindByScreenPoint(double screenX, double screenY);
    BoundGameWindow? BindByHandle(nint hwnd);
    BoundGameWindow? InspectByHandle(nint hwnd);
    BoundGameWindow? BindFirstGameWindow();
    IReadOnlyList<BoundGameWindow> DiscoverGameWindows() =>
        Current is null ? Array.Empty<BoundGameWindow>() : new[] { Current };
    bool TryScreenToClient(BoundGameWindow window, double screenX, double screenY, out int clientX, out int clientY, out string error);

    void Unbind();
}
public interface ILogService
{
    IReadOnlyList<LogEntry> Entries { get; }
    event EventHandler<LogEntry>? Appended;
    event EventHandler? Cleared;

    void Log(LogEntry entry);
    void Log(LogLevel level, string module, string message, string window = "-");
    void Trace(string module, string message, string window = "-");
    void Info(string module, string message, string window = "-");
    void Warn(string module, string message, string window = "-");
    void Error(string module, string message, string window = "-");
    IDisposable Suppress(Func<LogEntry, bool> predicate);
    void Clear();
    string ExportToText();
}

public interface ICombatLoopTelemetryService
{
    string CurrentFilePath { get; }
    void Record(CombatLoopTelemetryEntry entry);
}

public interface IProfileService
{
    IReadOnlyList<string> ListProfileNames();
    string? CurrentProfileName { get; }
    void Save(string name, string jsonPayload);
    string? Load(string name);
    void Delete(string name);
}

public sealed record AppProfileLoadResult(
    string ProfileName,
    bool GlobalLoaded,
    IReadOnlyList<string> LoadedModules,
    IReadOnlyList<string> MissingModules);

public interface IAppProfileService
{
    IReadOnlyList<string> ListProfileNames();
    string? CurrentProfileName { get; }
    void SaveAll(string profileName);
    void RenameAll(string oldProfileName, string newProfileName);
    AppProfileLoadResult LoadAll(string profileName);
    string? ResolveProfileForWindow(BoundGameWindow window);
    void RememberWindowProfile(BoundGameWindow window, string profileName);
}
public interface IModuleCatalog
{
    IReadOnlyList<ModuleDescriptor> Modules { get; }
    IEnumerable<ModuleDescriptor> ByGroup(string group);
}

public enum MainlineReadinessSeverity
{
    Info = 0,
    Warning,
    Error
}

public sealed record MainlineReadinessItem(
    MainlineReadinessSeverity Severity,
    string Feature,
    string Message)
{
    public string ActionArea { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public bool BlocksStart => Severity == MainlineReadinessSeverity.Error;
}

public sealed record MainlineReadinessReport(
    bool CanStart,
    IReadOnlyList<MainlineReadinessItem> Items)
{
    public IReadOnlyList<MainlineReadinessItem> Errors =>
        Items.Where(i => i.Severity == MainlineReadinessSeverity.Error).ToArray();

    public IReadOnlyList<MainlineReadinessItem> Warnings =>
        Items.Where(i => i.Severity == MainlineReadinessSeverity.Warning).ToArray();

    public IReadOnlyList<MainlineReadinessItem> Infos =>
        Items.Where(i => i.Severity == MainlineReadinessSeverity.Info).ToArray();
}

public interface IMainlineReadinessService
{
    MainlineReadinessReport Inspect(
        GlobalConfig config,
        BoundGameWindow? window,
        bool includeReturnPointReadiness = true,
        bool includeLiveReturnRecognition = false);
}
