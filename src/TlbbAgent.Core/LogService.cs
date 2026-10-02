using System.Collections.Concurrent;
using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RuntimeLogService : ILogService
{
    private const int MaxEntries = 5000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly AsyncLocal<List<Func<LogEntry, bool>>?> _suppressors = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public event EventHandler<LogEntry>? Appended;
    public event EventHandler? Cleared;

    public void Log(LogEntry entry)
    {
        entry = NormalizeAudience(entry);
        if (ShouldSuppress(entry))
            return;

        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _)) { }
        var handlers = Appended;
        if (handlers is null) return;

        foreach (EventHandler<LogEntry> handler in handlers.GetInvocationList())
        {
            try { handler(this, entry); }
            catch { /* 日志订阅者不能影响自动化运行 */ }
        }
    }

    public void Log(LogLevel level, string module, string message, string window = "-")
        => Log(new LogEntry { Level = level, Module = module, Message = message, Window = window });

    public void Trace(string module, string message, string window = "-")
        => Log(LogLevel.Trace, module, message, window);

    public void Info(string module, string message, string window = "-")
        => Log(LogLevel.Info, module, message, window);

    public void Warn(string module, string message, string window = "-")
        => Log(LogLevel.Warn, module, message, window);

    public void Error(string module, string message, string window = "-")
        => Log(LogLevel.Error, module, message, window);

    public IDisposable Suppress(Func<LogEntry, bool> predicate)
    {
        var list = _suppressors.Value ??= new List<Func<LogEntry, bool>>();
        list.Add(predicate);
        return new SuppressionScope(this, predicate);
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
        var handlers = Cleared;
        if (handlers is null) return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch { /* 日志订阅者不能影响自动化运行 */ }
        }
    }

    public string ExportToText()
    {
        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
            if (!UserLogPrivacyPolicy.TryGetUserMessage(entry, out var message))
                continue;

            sb.AppendLine($"{entry.Time:HH:mm:ss}  {UserLogPrivacyPolicy.LevelText(entry.Level),-4}  {message}");
        }

        return sb.ToString();
    }

    private bool ShouldSuppress(LogEntry entry)
    {
        var list = _suppressors.Value;
        return list is not null && list.Any(predicate => predicate(entry));
    }

    private static LogEntry NormalizeAudience(LogEntry entry)
    {
        var containsTechnicalDetails = UserLogPrivacyPolicy.ContainsTechnicalDetails(entry.Module, entry.Message)
                                       || UserLogPrivacyPolicy.ContainsTechnicalDetails(entry.Module, entry.UserSummary);
        var audience = entry.Audience switch
        {
            LogAudience.Diagnostic => LogAudience.Diagnostic,
            LogAudience.User => LogAudience.User,
            _ when entry.Level == LogLevel.Trace => LogAudience.Diagnostic,
            _ when containsTechnicalDetails && entry.Level < LogLevel.Warn => LogAudience.Diagnostic,
            _ => LogAudience.User
        };
        var userSummary = audience == LogAudience.User
            ? UserLogPrivacyPolicy.CreateUserSummary(entry, containsTechnicalDetails)
            : null;

        return new LogEntry
        {
            Time = entry.Time,
            Window = entry.Window,
            Module = entry.Module,
            Level = entry.Level,
            Message = entry.Message,
            Audience = audience,
            UserSummary = userSummary,
            DebugDetails = containsTechnicalDetails
                ? string.IsNullOrWhiteSpace(entry.DebugDetails) ? entry.Message : entry.DebugDetails
                : entry.DebugDetails,
            RoleSessionId = entry.RoleSessionId,
            FeatureKey = entry.FeatureKey,
            CommandId = entry.CommandId
        };
    }

    private sealed class SuppressionScope(RuntimeLogService owner, Func<LogEntry, bool> predicate) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner._suppressors.Value?.Remove(predicate);
        }
    }
}
