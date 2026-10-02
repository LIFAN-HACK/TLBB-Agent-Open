using System.Diagnostics;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RuntimeWarmupService : IRuntimeWarmupService
{
    private readonly IOpenTextRecognizer _openText;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private Task? _warmupTask;

    public RuntimeWarmupService(IOpenTextRecognizer openText, ILogService log)
    {
        _openText = openText;
        _log = log;
    }

    public Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _warmupTask ??= Task.Run(
                () => Warmup(cancellationToken),
                CancellationToken.None);
        }
    }

    private void Warmup(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var ready = _openText.TryWarmup(out var message);
            stopwatch.Stop();
            if (ready)
            {
                _log.Info(
                    "运行预热",
                    $"文字识别已预热，耗时 {stopwatch.Elapsed.TotalMilliseconds:0}ms。{message}");
            }
            else
            {
                _log.Warn(
                    "运行预热",
                    $"文字识别预热未完成，运行时将保持安全降级。{message}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("运行预热", $"文字识别预热失败，运行时将保持安全降级：{ex.Message}");
        }
    }
}
