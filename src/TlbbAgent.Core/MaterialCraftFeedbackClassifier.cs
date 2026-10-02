using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public enum MaterialCraftFeedbackKind
{
    Unknown = 0,
    Busy,
    Completed,
    Exhausted,
    Cooldown,
    InsufficientResource,
    InvalidAction
}

public static class MaterialCraftFeedbackClassifier
{
    private static readonly string[] ExhaustedKeywords =
    {
        "材料不足",
        "缺少材料",
        "材料不够",
        "所需材料不足",
        "原料不足",
        "原料不够"
    };

    private static readonly string[] BusyKeywords =
    {
        "正在制造",
        "正在加工",
        "制造中",
        "加工中",
        "制作中"
    };

    private static readonly string[] CompletedKeywords =
    {
        "你做了一个",
        "制造成功",
        "加工成功",
        "制作成功"
    };

    private static readonly string[] CooldownKeywords =
    {
        "冷却时间未到",
        "技能尚未冷却",
        "冷却中"
    };

    private static readonly string[] InsufficientResourceKeywords =
    {
        "活力不足",
        "精力不足",
        "真气不足",
        "气不足",
        "体力不足"
    };

    private static readonly string[] InvalidActionKeywords =
    {
        "当前操作未完成",
        "无法进行此操作",
        "不能进行此操作",
        "无效目标",
        "目标无效"
    };

    public static MaterialCraftFeedbackKind Classify(string? text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
            return MaterialCraftFeedbackKind.Unknown;

        if (CompletedKeywords.Any(normalized.Contains))
            return MaterialCraftFeedbackKind.Completed;

        if (BusyKeywords.Any(normalized.Contains))
            return MaterialCraftFeedbackKind.Busy;

        if (ExhaustedKeywords.Any(normalized.Contains))
            return MaterialCraftFeedbackKind.Exhausted;

        if (CooldownKeywords.Any(normalized.Contains))
            return MaterialCraftFeedbackKind.Cooldown;

        if (InsufficientResourceKeywords.Any(normalized.Contains))
            return MaterialCraftFeedbackKind.InsufficientResource;

        return InvalidActionKeywords.Any(normalized.Contains)
            ? MaterialCraftFeedbackKind.InvalidAction
            : MaterialCraftFeedbackKind.Unknown;
    }

    public static SystemFeedbackSemanticKind ClassifySemantic(string? text) => Classify(text) switch
    {
        MaterialCraftFeedbackKind.Exhausted => SystemFeedbackSemanticKind.MaterialExhausted,
        MaterialCraftFeedbackKind.Busy => SystemFeedbackSemanticKind.MaterialBusy,
        MaterialCraftFeedbackKind.Completed => SystemFeedbackSemanticKind.MaterialCompleted,
        MaterialCraftFeedbackKind.Cooldown => SystemFeedbackSemanticKind.Cooldown,
        MaterialCraftFeedbackKind.InsufficientResource => SystemFeedbackSemanticKind.InsufficientResource,
        MaterialCraftFeedbackKind.InvalidAction => SystemFeedbackSemanticKind.InvalidAction,
        _ => SystemFeedbackSemanticKind.Unknown
    };

    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character))
                continue;

            builder.Append(character);
        }

        return builder.ToString();
    }
}
