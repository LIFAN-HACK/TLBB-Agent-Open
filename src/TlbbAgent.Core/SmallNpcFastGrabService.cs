using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class SmallNpcFastGrabService : ISmallNpcFastGrabService
{
    private const string Module = "SmallNpcFastGrab";
    private readonly ILogService _log;
    private readonly Dictionary<string, int> _contextAttempts = new(StringComparer.Ordinal);
    private NpcTargetingResult? _lastSmallContext;
    private DateTime _lastSmallContextAt;

    public SmallNpcFastGrabService(ILogService log) => _log = log;

    public SmallNpcFastGrabPlan Plan(NpcGrabConfig config)
    {
        var small = config.SmallNpcFastGrab ?? new SmallNpcFastGrabConfig();
        var refreshKey = CurrentRefreshWindowKey(small, DateTime.Now);
        var inBurst = small.EnableRefreshBurst && refreshKey is not null;
        var nextDelay = inBurst
            ? Math.Max(30, small.BurstScanIntervalMs)
            : Math.Max(50, config.ScanIntervalMs);
        var activeRecognitionAllowed = small.Strategy != SmallNpcGrabStrategy.ContextFastOnly;

        if (small.Strategy == SmallNpcGrabStrategy.AutoRecognizeOnly || !small.EnableContextFastAction)
        {
            return new SmallNpcFastGrabPlan(
                false,
                null,
                nextDelay,
                inBurst,
                activeRecognitionAllowed,
                "主动识别通道");
        }

        if (!HasValidContext(small))
        {
            return new SmallNpcFastGrabPlan(
                false,
                null,
                nextDelay,
                inBurst,
                activeRecognitionAllowed,
                "无有效小小上下文");
        }

        if (small.ContextFastOnlyInRefreshWindow && !inBurst)
        {
            return new SmallNpcFastGrabPlan(
                false,
                null,
                nextDelay,
                false,
                activeRecognitionAllowed,
                "上下文存在，但当前不在刷新窗口");
        }

        var key = refreshKey ?? "context";
        var attempts = _contextAttempts.GetValueOrDefault(key);
        if (attempts >= Math.Max(0, small.MaxContextFastAttemptsPerWindow))
        {
            return new SmallNpcFastGrabPlan(
                false,
                null,
                nextDelay,
                inBurst,
                activeRecognitionAllowed,
                "本刷新窗口上下文快抢次数已达上限");
        }

        var context = _lastSmallContext;
        if (context is null)
        {
            return new SmallNpcFastGrabPlan(
                false,
                null,
                nextDelay,
                inBurst,
                activeRecognitionAllowed,
                "小小上下文已失效");
        }

        _contextAttempts[key] = attempts + 1;
        var target = context with
        {
            Kind = NpcGrabTargetKind.Small2,
            DisplayName = "抢小2（上下文快抢）",
            MatchedKeyword = "context-fast"
        };

        return new SmallNpcFastGrabPlan(
            true,
            target,
            nextDelay,
            inBurst,
            activeRecognitionAllowed,
            "使用上下文快抢");
    }

    public void ObserveActionResult(NpcTargetingResult target, NpcActionResult result)
    {
        if (!result.Executed)
            return;

        if (IsSmallNpcTarget(target.Kind))
        {
            _lastSmallContext = target;
            _lastSmallContextAt = DateTime.Now;
            _log.Trace(Module, $"记录小小上下文：{target.DisplayName} / {target.MatchedKeyword}。");
            return;
        }

        if (target.Found)
        {
            _lastSmallContext = null;
            _contextAttempts.Clear();
            _log.Trace(Module, $"非小小目标动作已执行，清除上下文：{target.DisplayName}。");
        }
    }

    public bool IsSmallNpcTarget(NpcGrabTargetKind? kind) =>
        kind is NpcGrabTargetKind.Small or NpcGrabTargetKind.SmallDan or NpcGrabTargetKind.Small2;

    private bool HasValidContext(SmallNpcFastGrabConfig config)
    {
        if (_lastSmallContext is null)
            return false;

        var age = DateTime.Now - _lastSmallContextAt;
        return age.TotalSeconds <= Math.Max(1, config.ContextValidSeconds);
    }

    private static string? CurrentRefreshWindowKey(SmallNpcFastGrabConfig config, DateTime now)
    {
        foreach (var minute in ParseMinuteMarks(config.RefreshMinuteMarks))
        {
            var mark = new DateTime(now.Year, now.Month, now.Day, now.Hour, minute, 0);
            foreach (var candidate in new[] { mark.AddHours(-1), mark, mark.AddHours(1) })
            {
                var seconds = (now - candidate).TotalSeconds;
                if (seconds >= -Math.Max(0, config.RefreshWindowBeforeSeconds)
                    && seconds <= Math.Max(0, config.RefreshWindowAfterSeconds))
                {
                    return $"{candidate:yyyyMMddHHmm}";
                }
            }
        }

        return null;
    }

    private static IEnumerable<int> ParseMinuteMarks(string text)
    {
        var parts = text.Split(
            new[] { ';', '；', ',', '，', '|', '/', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (int.TryParse(part, out var minute) && minute is >= 0 and <= 59)
                yield return minute;
        }
    }
}
