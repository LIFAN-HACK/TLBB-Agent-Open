using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class NpcTargetingService : INpcTargetingService
{
    private const string Module = "NpcTargeting";
    private readonly IPerceptionHub _perception;
    private readonly IGlobalConfigService _global;
    private readonly ILogService _log;

    public NpcTargetingService(IPerceptionHub perception, IGlobalConfigService global, ILogService log)
    {
        _perception = perception;
        _global = global;
        _log = log;
    }

    public NpcTargetingResult SelectTarget(BoundGameWindow window, NpcGrabConfig config)
    {
        var enabled = config.Targets
            .Where(t => t.Enabled)
            .OrderBy(t => t.Priority)
            .ToList();

        if (enabled.Count == 0)
        {
            _log.Warn(Module, "抢 NPC 未启用任何目标。", window.Id);
            return NpcTargetingResult.Empty();
        }

        var snapshot = _perception.Read(window, _global.Current, includeOpenText: true);
        var text = snapshot.Target.NameText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Trace(Module, "未识别目标。", window.Id);
            return NpcTargetingResult.Empty();
        }

        foreach (var target in enabled)
        {
            var keywords = SplitKeywords(target.Keywords);
            foreach (var keyword in keywords)
            {
                if (!text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                _log.Info(Module, $"匹配目标：{target.DisplayName}", window.Id);
                return new NpcTargetingResult(true, target.Kind, target.DisplayName, text, keyword);
            }
        }

        _log.Trace(Module, "未匹配目标。", window.Id);
        return NpcTargetingResult.Empty(text);
    }

    private static IEnumerable<string> SplitKeywords(string text) =>
        text.Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsUsefulKeyword);

    private static bool IsUsefulKeyword(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2)
            return true;

        return false;
    }
}
