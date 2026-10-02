using System.Text.Encodings.Web;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class CombatLoopTelemetryService : ICombatLoopTelemetryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly IAppInitService _init;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private long _lastFailureTicks;

    public CombatLoopTelemetryService(IAppInitService init, ILogService log)
    {
        _init = init;
        _log = log;
    }

    public string CurrentFilePath =>
        IsDeveloperTelemetryEnabled
            ? Path.Combine(_init.Paths.Logs, $"combat-loop-{DateTime.Now:yyyyMMdd}.jsonl")
            : string.Empty;

    public void Record(CombatLoopTelemetryEntry entry)
    {
        if (!IsDeveloperTelemetryEnabled)
            return;

        try
        {
            Directory.CreateDirectory(_init.Paths.Logs);
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (_gate)
            {
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (_lastFailureTicks != 0 && now - _lastFailureTicks < 5000)
                return;

            _lastFailureTicks = now;
            _log.Log(new LogEntry
            {
                Level = LogLevel.Warn,
                Module = "Telemetry",
                Message = "内部诊断记录失败。",
                Audience = LogAudience.Diagnostic,
                DebugDetails = ex.Message
            });
        }
    }

    private static bool IsDeveloperTelemetryEnabled
    {
        get
        {
#if DEBUG
            return string.Equals(
                Environment.GetEnvironmentVariable("TLBB_AGENT_INTERNAL_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal);
#else
            return false;
#endif
        }
    }
}
