using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public static class CombatLoopHealthAnalyzer
{
    public static CombatLoopHealthReport Analyze(IEnumerable<CombatLoopTelemetryEntry> entries)
    {
        var rows = entries.Where(e => e is not null).ToArray();
        var decisions = rows.Where(e => IsEvent(e, "decision")).ToArray();
        var actions = rows.Where(e => IsEvent(e, "action")).ToArray();
        var legacySchema = rows.Count(e => e.SchemaVersion < 2);

        var sentAttack = actions.Count(e => Contains(e.AttackResult, "已发送"));
        var waiting = actions.Count(e => Contains(e.AttackResult, "等待") || Contains(e.AttackResult, "间隔"));
        var sendFailed = actions.Count(e =>
            Contains(e.AttackResult, "失败")
            || Contains(e.AttackResult, "无效")
            || Contains(e.AttackResult, "未配置"));
        var noAttackAction = actions.Count(e => string.IsNullOrWhiteSpace(e.AttackResult) && !e.SkillSent);
        var protection = decisions.Count(e =>
            Contains(e.Decision, "保护")
            || e.TargetLooksLikePlayer
            || e.TargetLooksLikePet);
        var ambiguousProtection = decisions.Count(e => e.TargetProtectionAmbiguous);
        var weakOnly = decisions.Count(IsWeakOnlyTarget);
        var weakNoStructure = decisions.Count(e => IsWeakOnlyTarget(e) && !HasAnyTargetStructure(e));
        var protectionWithoutSample = decisions.Count(e =>
            (Contains(e.Decision, "保护") || e.TargetLooksLikePlayer || e.TargetLooksLikePet)
            && string.IsNullOrWhiteSpace(e.TargetSampleKind));
        var sampleEmpty = decisions.Count(e => string.IsNullOrWhiteSpace(e.TargetSampleKind));

        var findings = BuildFindings(
            decisions.Length,
            actions.Length,
            sentAttack,
            waiting,
            sendFailed,
            protection,
            ambiguousProtection,
            weakOnly,
            weakNoStructure,
            protectionWithoutSample,
            sampleEmpty,
            noAttackAction,
            legacySchema).ToArray();

        return new CombatLoopHealthReport(
            rows.Length,
            legacySchema,
            decisions.Length,
            actions.Length,
            sentAttack,
            waiting,
            sendFailed,
            protection,
            ambiguousProtection,
            weakOnly,
            weakNoStructure,
            protectionWithoutSample,
            sampleEmpty,
            noAttackAction,
            CountBy(rows, e => e.Decision),
            CountBy(rows, e => e.AttackResult),
            CountBy(rows, e => e.TargetSampleKind),
            CountBy(rows, TargetStructureLabel),
            CountBy(rows, e => e.Event),
            findings);
    }

    private static IEnumerable<CombatLoopHealthFinding> BuildFindings(
        int decisions,
        int actions,
        int sentAttack,
        int waiting,
        int sendFailed,
        int protection,
        int ambiguousProtection,
        int weakOnly,
        int weakNoStructure,
        int protectionWithoutSample,
        int sampleEmpty,
        int noAttackAction,
        int legacySchema = 0)
    {
        if (decisions == 0)
        {
            yield return new("no-decision", CombatLoopHealthSeverity.Error, "没有 decision 记录：控制器可能未启动，或遥测未写入。");
            yield break;
        }

        if (actions == 0)
            yield return new("no-action", CombatLoopHealthSeverity.Warn, "没有 action 记录：Planner 一直没有进入实际普攻/技能分支，优先看 Decision 分布。");

        if (legacySchema > Math.Max(3, decisions / 2))
            yield return new("legacy-telemetry-schema", CombatLoopHealthSeverity.Info, "多数遥测来自旧版本日志，缺少目标结构字段；请重新运行当前版本后再判断 TargetStructure/玩家保护误判。");

        if (sentAttack == 0 && actions > 0)
            yield return new("no-sent-attack", CombatLoopHealthSeverity.Error, "没有成功普攻：检查 AttackResult 是否为未配置、无效、发送失败或一直等待间隔。");

        if (sendFailed > 0)
            yield return new("input-send-failed", CombatLoopHealthSeverity.Error, "存在输入发送失败/热键无效/未配置：优先检查普通攻击键、输入模式和窗口焦点。");

        if (protection > Math.Max(3, decisions / 3))
            yield return new("protection-heavy", CombatLoopHealthSeverity.Warn, "保护目标占比过高：可能正在选到玩家/宠物，或玩家保护图色误判；应查看 TargetSampleKind 和采样模板。");

        if (ambiguousProtection > 0)
            yield return new("ambiguous-protection", CombatLoopHealthSeverity.Info, "存在疑似玩家目标：当前不会硬拦截，但应采集玩家/怪物样本，提高保护准确度。");

        if (weakOnly > Math.Max(3, decisions / 2))
            yield return new("weak-only-heavy", CombatLoopHealthSeverity.Warn, "弱血条占比过高：目标名/样本/可攻击标记不足，优先校准目标面板区域和样本。");

        if (weakNoStructure >= Math.Max(3, decisions / 3))
            yield return new("weak-no-structure-heavy", CombatLoopHealthSeverity.Warn, "大量弱血条没有目标结构信号：优先检查目标面板识别区是否偏移，或当前分辨率画像是否不匹配。");

        if (sampleEmpty >= Math.Max(5, decisions * 2 / 3))
            yield return new("sample-coverage-low", CombatLoopHealthSeverity.Warn, "大部分目标没有样本命中：应采集/导入玩家、怪物、BOSS、宠物、空目标样本，否则保护与可攻击判断会抖动。");

        if (protectionWithoutSample > Math.Max(3, protection / 2) && protection > 0)
            yield return new("protection-without-sample", CombatLoopHealthSeverity.Warn, "保护目标多数没有样本支撑：可能是结构图色误判玩家，需查看 TargetStructure 并补玩家/怪物样本。");

        if (waiting > sentAttack * 3 && waiting > 5)
            yield return new("attack-waiting-heavy", CombatLoopHealthSeverity.Warn, "等待/间隔记录明显偏多：可能普攻间隔过大、技能 CD 监控未命中，或当前目标生命周期未刷新。");

        if (noAttackAction > 0)
            yield return new("action-without-output", CombatLoopHealthSeverity.Warn, "存在 action 但无普攻/技能：应检查技能配置、普通攻击键和公共 CD。");

        if (sentAttack > 0 && protection < Math.Max(3, decisions / 3) && weakOnly < Math.Max(3, decisions / 2))
            yield return new("attack-chain-ok", CombatLoopHealthSeverity.Info, "基础攻击链路有成功普攻；如果现场仍不掉血，优先检查输入是否被客户端接收、目标距离/可攻击性和技能命中。");
    }

    private static bool IsWeakOnlyTarget(CombatLoopTelemetryEntry entry) =>
        entry.TargetHp is > 0
        && string.IsNullOrWhiteSpace(entry.TargetName)
        && string.IsNullOrWhiteSpace(entry.TargetSampleKind)
        && !entry.TargetLooksLikePlayer
        && !entry.TargetLooksLikePet
        && entry.TargetIsAttackable is null;

    private static bool HasAnyTargetStructure(CombatLoopTelemetryEntry entry) =>
        entry.TargetHasSchoolMarker || entry.TargetHasPlayerPortrait || entry.TargetHasPlayerStatusBars;

    private static IReadOnlyDictionary<string, int> CountBy(
        IReadOnlyList<CombatLoopTelemetryEntry> entries,
        Func<CombatLoopTelemetryEntry, string> selector) =>
        entries
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string TargetStructureLabel(CombatLoopTelemetryEntry entry)
    {
        var parts = new List<string>();
        if (entry.TargetHasSchoolMarker)
            parts.Add("school");
        if (entry.TargetHasPlayerPortrait)
            parts.Add("portrait");
        if (entry.TargetHasPlayerStatusBars)
            parts.Add("bars");
        return parts.Count == 0 ? "none" : string.Join("+", parts);
    }

    private static bool IsEvent(CombatLoopTelemetryEntry entry, string value) =>
        string.Equals(entry.Event, value, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string part) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(part, StringComparison.Ordinal);
}

public sealed record CombatLoopHealthReport(
    int TotalRows,
    int LegacySchemaCount,
    int DecisionCount,
    int ActionCount,
    int SentAttackCount,
    int WaitingCount,
    int SendFailedCount,
    int ProtectionCount,
    int AmbiguousProtectionCount,
    int WeakOnlyCount,
    int WeakNoStructureCount,
    int ProtectionWithoutSampleCount,
    int SampleEmptyCount,
    int NoAttackActionCount,
    IReadOnlyDictionary<string, int> DecisionCounts,
    IReadOnlyDictionary<string, int> AttackResultCounts,
    IReadOnlyDictionary<string, int> TargetSampleKindCounts,
    IReadOnlyDictionary<string, int> TargetStructureCounts,
    IReadOnlyDictionary<string, int> EventCounts,
    IReadOnlyList<CombatLoopHealthFinding> Findings)
{
    public bool HasError => Findings.Any(f => f.Severity == CombatLoopHealthSeverity.Error);
}

public sealed record CombatLoopHealthFinding(
    string Code,
    CombatLoopHealthSeverity Severity,
    string Message);

public enum CombatLoopHealthSeverity
{
    Info,
    Warn,
    Error
}
