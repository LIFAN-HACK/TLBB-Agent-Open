using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class TargetSafetyService : ITargetSafetyService
{
    public TargetSafetyDecision Evaluate(
        GlobalConfig config,
        CombatRecognitionResult combat,
        TargetSafetyOptions? options = null)
    {
        options ??= new TargetSafetyOptions();

        if (!combat.HasTarget)
            return TargetSafetyDecision.Reject("未识别到目标", "未识别到目标", isProtectedTarget: false);

        if (combat.TargetProtectionAmbiguous)
        {
            return RejectProtected(
                "疑似玩家目标保护",
                "目标具有人物面板特征，但身份尚未可靠确认；本轮不攻击并切换目标。");
        }

        if (combat.TargetLooksLikePet)
            return RejectProtected("宠物目标保护", "识别到宠物或召唤物目标，本轮不攻击并切换目标。");

        if (combat.TargetIsAttackable == false)
            return RejectProtected("目标不可攻击", "视觉结果明确标记当前目标不可攻击。");

        if (config.Targeting.Policy?.Enabled == true)
        {
            var policyDecision = EvaluateUnifiedPolicy(config, combat, options);
            if (!policyDecision.Allowed)
                return policyDecision;
        }
        else
        {
            if (config.Targeting.BlockPlayerTargets && combat.TargetLooksLikePlayer)
                return RejectProtected("玩家目标保护", "识别到玩家目标；统一目标策略未开启，默认禁止攻击玩家。");

            if (MatchesAny(config.Targeting.TargetNameBlacklist, combat.TargetNameText, conservativeDeny: true))
                return RejectPolicy("旧黑名单命中", $"目标名称命中旧黑名单：{combat.TargetNameText}");

            if (config.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist
                && !MatchesAny(config.Targeting.TargetNameWhitelist, combat.TargetNameText, conservativeDeny: false))
            {
                return RejectPolicy("BOSS 白名单未命中", $"BOSS 模式仅攻击白名单目标，当前：{combat.TargetNameText}");
            }
        }

        if (!HasActionableTarget(combat, options))
        {
            var reason = combat.HasTargetHp ? "仅识别到弱目标血条" : "未识别到目标";
            return TargetSafetyDecision.Reject(reason, reason, isProtectedTarget: false);
        }

        var weakKnownTargetAllowed = options.AllowWeakKnownTarget
                                     && options.HadTarget
                                     && options.HasConfirmedIdentity
                                     && combat.HasTargetHp;
        if (RequiresRecognizedTarget(config) && !HasStrongTargetSignal(combat) && !weakKnownTargetAllowed)
        {
            return TargetSafetyDecision.Reject(
                "目标识别不足",
                "目标识别不足",
                isProtectedTarget: false,
                resetTargetProgress: true,
                warningLabel: "目标识别不足",
                warningDetail: "当前策略依赖目标身份或名称，但本帧只有弱血条信号；不攻击并重新选怪。");
        }

        if (options.IncludeStrategyFilters)
        {
            if (config.Targeting.OnlyAttackMarked)
            {
                return TargetSafetyDecision.Reject(
                    "标记目标识别尚未接入",
                    "目标策略不匹配",
                    isProtectedTarget: false,
                    unsupportedStrategyWarning: "只攻击标记目标需要独立标记识别模型，当前不执行攻击。");
            }

            if (config.Targeting.Filter == TargetFilter.RedNameOnly)
            {
                return TargetSafetyDecision.Reject(
                    "红名目标识别尚未接入",
                    "目标策略不匹配",
                    isProtectedTarget: false,
                    unsupportedStrategyWarning: "只打红名需要可靠颜色/身份模型，当前不执行攻击。");
            }

            if (config.Targeting.Filter == TargetFilter.SpecificName
                && !MatchesSpecificTarget(config.Targeting.SpecificName, combat.TargetNameText))
            {
                return TargetSafetyDecision.Reject("目标名称不匹配", "目标策略不匹配", isProtectedTarget: false);
            }
        }

        return TargetSafetyDecision.Allow();
    }

    public bool HasActionableTarget(CombatRecognitionResult combat, TargetSafetyOptions? options = null)
    {
        options ??= new TargetSafetyOptions();
        if (!combat.HasTarget)
            return false;
        if (HasStrongTargetSignal(combat))
            return true;

        return options.AllowWeakKnownTarget
               && options.HadTarget
               && options.HasConfirmedIdentity
               && combat.HasTargetHp;
    }

    public bool HasStrongTargetSignal(CombatRecognitionResult combat) =>
        combat.HasTargetName
        || IsKnownAttackableSample(combat.TargetSampleKind)
        || combat.TargetLooksLikePlayer
        || combat.TargetLooksLikePet
        || combat.TargetIsAttackable == true;

    public bool RequiresRecognizedTarget(GlobalConfig config)
    {
        if (config.Targeting.OnlyAttackMarked
            || config.Targeting.Filter is TargetFilter.RedNameOnly or TargetFilter.SpecificName)
        {
            return true;
        }

        var policy = config.Targeting.Policy;
        if (policy?.Enabled != true)
            return config.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist;

        if (config.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist)
            return true;

        return policy.Categories
            .Where(category => category.Enabled)
            .Any(category => !category.AllowUnlisted || category.Entries.Any(entry => entry.Enabled));
    }

    private static TargetSafetyDecision EvaluateUnifiedPolicy(
        GlobalConfig config,
        CombatRecognitionResult combat,
        TargetSafetyOptions options)
    {
        var policy = config.Targeting.Policy;
        var categories = policy.Categories ?? new List<TargetPolicyCategoryConfig>();
        var hasEnabledNameRules = categories
            .Where(category => category.Enabled)
            .SelectMany(category => category.Entries ?? new List<TargetPolicyEntry>())
            .Any(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Name));
        if (hasEnabledNameRules && !combat.HasTargetName && !options.HasConfirmedIdentity)
        {
            return TargetSafetyDecision.Reject(
                "目标名称待确认",
                "目标名称待确认",
                isProtectedTarget: false,
                resetTargetProgress: false);
        }

        var matchingEntries = categories
            .SelectMany(category => (category.Entries ?? new List<TargetPolicyEntry>())
                .Where(entry => entry.Enabled
                                && TargetNamePolicyMatcher.IsMatch(
                                    entry.Name,
                                    combat.TargetNameText,
                                    conservativeDeny: !entry.AllowAttack))
                .Select(entry => (Category: category, Entry: entry)))
            .ToList();

        var denied = matchingEntries.FirstOrDefault(item => !item.Entry.AllowAttack);
        if (denied.Entry is not null)
        {
            return RejectPolicy(
                $"{CategoryText(denied.Category.Category)}禁止目标",
                $"目标“{combat.TargetNameText}”命中禁止攻击规则“{denied.Entry.Name}”。");
        }

        var categoryKind = ResolveCategory(combat, matchingEntries);
        var category = categories.FirstOrDefault(item => item.Category == categoryKind);
        if (category is null || !category.Enabled)
        {
            return categoryKind == TargetPolicyCategoryKind.Player
                ? RejectProtected("玩家分类未启用", "玩家目标分类默认关闭，本轮不攻击并切换目标。")
                : RejectPolicy($"{CategoryText(categoryKind)}分类未启用", "当前目标所属分类已关闭。");
        }

        if (categoryKind == TargetPolicyCategoryKind.Player)
        {
            if (!combat.TargetHostilityEvidenceAvailable || !combat.TargetAggressionEvidenceAvailable)
            {
                return RejectProtected(
                    "玩家反击证据不可用",
                    "红名身份或近期主动伤害证据尚不可用；本轮保持保护，禁止攻击玩家。");
            }

            if (!combat.TargetIsHostileRedName || !combat.TargetRecentlyDamagedPlayer)
            {
                return RejectProtected(
                    "玩家反击条件不足",
                    "只有同时确认红名并且该目标近期主动伤害我方时才允许反击；当前证据不足，避免害刀。");
            }
        }

        if (config.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist
            && categoryKind != TargetPolicyCategoryKind.Boss)
        {
            return RejectPolicy("非 BOSS 目标", "BOSS 模式只允许攻击 BOSS 分类目标。");
        }

        var allowedEntry = matchingEntries.FirstOrDefault(item => item.Entry.AllowAttack && item.Category.Category == categoryKind);
        if (allowedEntry.Entry is not null)
            return TargetSafetyDecision.Allow();

        if (!category.AllowUnlisted)
        {
            return RejectPolicy(
                $"{CategoryText(categoryKind)}未列入允许项",
                $"目标“{combat.TargetNameText}”不在允许攻击列表中。");
        }

        return TargetSafetyDecision.Allow();
    }

    private static TargetPolicyCategoryKind ResolveCategory(
        CombatRecognitionResult combat,
        IReadOnlyList<(TargetPolicyCategoryConfig Category, TargetPolicyEntry Entry)> matchingEntries)
    {
        if (combat.TargetLooksLikePlayer)
            return TargetPolicyCategoryKind.Player;

        if (combat.TargetSampleKind.Equals("boss", StringComparison.OrdinalIgnoreCase)
            || combat.TargetSampleKind.Equals("elite", StringComparison.OrdinalIgnoreCase))
        {
            return TargetPolicyCategoryKind.Boss;
        }

        if (matchingEntries.Any(item => item.Category.Category == TargetPolicyCategoryKind.Boss))
            return TargetPolicyCategoryKind.Boss;
        if (matchingEntries.Any(item => item.Category.Category == TargetPolicyCategoryKind.Player))
            return TargetPolicyCategoryKind.Player;

        return TargetPolicyCategoryKind.Monster;
    }

    private static TargetSafetyDecision RejectProtected(string label, string detail) =>
        TargetSafetyDecision.Reject(
            label,
            label,
            isProtectedTarget: true,
            resetTargetProgress: true,
            warningLabel: label,
            warningDetail: detail);

    private static TargetSafetyDecision RejectPolicy(string label, string detail) =>
        TargetSafetyDecision.Reject(
            label,
            label,
            isProtectedTarget: false,
            resetTargetProgress: true,
            warningLabel: label,
            warningDetail: detail);

    private static string CategoryText(TargetPolicyCategoryKind category) => category switch
    {
        TargetPolicyCategoryKind.Boss => "BOSS",
        TargetPolicyCategoryKind.Player => "玩家",
        _ => "普通怪物"
    };

    private static bool MatchesSpecificTarget(string specificName, string targetName)
    {
        if (string.IsNullOrWhiteSpace(specificName))
            return true;
        if (string.IsNullOrWhiteSpace(targetName))
            return false;
        return SplitTargetNames(specificName)
            .Any(name => TargetNamePolicyMatcher.IsMatch(name, targetName, conservativeDeny: false));
    }

    private static bool MatchesAny(string list, string targetName, bool conservativeDeny) =>
        !string.IsNullOrWhiteSpace(list)
        && !string.IsNullOrWhiteSpace(targetName)
        && SplitTargetNames(list)
            .Any(name => TargetNamePolicyMatcher.IsMatch(name, targetName, conservativeDeny));

    private static IEnumerable<string> SplitTargetNames(string text) =>
        text.Split(new[] { ';', '；', ',', '，', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0);

    private static bool IsKnownAttackableSample(string kind) =>
        kind.Equals("monster", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("boss", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("elite", StringComparison.OrdinalIgnoreCase);
}

internal static class TargetNamePolicyMatcher
{
    public static bool IsMatch(string configuredName, string observedName, bool conservativeDeny)
    {
        var configured = Normalize(configuredName);
        var observed = Normalize(observedName);
        if (configured.Length == 0 || observed.Length == 0)
            return false;

        if (observed.Contains(configured, StringComparison.OrdinalIgnoreCase))
            return true;

        // Deny rules may absorb one OCR insertion, deletion, or substitution.
        // Allow rules remain exact so recognition noise cannot expand permission.
        return conservativeDeny
               && Math.Min(configured.Length, observed.Length) >= 4
               && IsEditDistanceAtMostOne(configured, observed);
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC);
        return new string(normalized.Where(char.IsLetterOrDigit).ToArray());
    }

    private static bool IsEditDistanceAtMostOne(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1)
            return false;

        if (left.Length == right.Length)
        {
            var differences = 0;
            for (var index = 0; index < left.Length; index++)
            {
                if (char.ToUpperInvariant(left[index]) == char.ToUpperInvariant(right[index]))
                    continue;
                if (++differences > 1)
                    return false;
            }

            return true;
        }

        var shorter = left.Length < right.Length ? left : right;
        var longer = left.Length < right.Length ? right : left;
        var shortIndex = 0;
        var longIndex = 0;
        var skipped = false;
        while (shortIndex < shorter.Length && longIndex < longer.Length)
        {
            if (char.ToUpperInvariant(shorter[shortIndex]) == char.ToUpperInvariant(longer[longIndex]))
            {
                shortIndex++;
                longIndex++;
                continue;
            }

            if (skipped)
                return false;
            skipped = true;
            longIndex++;
        }

        return true;
    }
}
