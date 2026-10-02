using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

/// <summary>
/// 用户日志的唯一隐私边界。原始消息可留在内存诊断条目中，但任何用户展示或导出
/// 都必须先经过本策略，并且只接受 Audience=User 且存在 UserSummary 的条目。
/// </summary>
public static class UserLogPrivacyPolicy
{
    private const int MaxUserMessageLength = 180;

    private static readonly string[] TechnicalModuleTokens =
    {
        "Ocr", "Vision", "Perception", "Recognition", "Resolution", "Telemetry", "Diagnostics",
        "Template", "OpenText", "Input", "Capture", "Backend", "RuntimeSnapshot", "ReadOnly",
        "RoleSession", "Model", "Worker", "Planner", "Engine", "SkillCooldown",
        "视觉", "识别", "遥测", "运行快照", "输入诊断", "模型", "字库", "截图", "采样"
    };

    private static readonly string[] TechnicalMessageTokens =
    {
        "PostMessage", "SendInput", "Win32", "HRESULT", "HWND", "ProcessId", "PID",
        "ONNX", "YOLO", "OpenText", "VisionV2", "Python", "CUDA", "DirectML",
        "TlbbAgent.",
        "worker", "engine", "runtime", "planner", "inference", "confidence", "score",
        "model version", "response id", "persistent host", "frame stream", "probe failed",
        "stack trace", "exception", "timeout", "异常", "失败",
        "object reference", "collection was modified", "sequence contains", "value cannot be null",
        "cannot access a disposed object", "out of range", "access denied", "permission denied",
        "not found", "invalid operation", "模型路径", "样本路径", "置信度", "阈值",
        "坐标", "识别区", "客户区", "当前坐标文本", "死亡识别", "关键词", "样本=",
        "目标血", "人物血", "角色血", "宠物血", "目标蓝", "人物蓝", "角色蓝", "宠物蓝",
        "可攻击", "结构判定", "覆盖检查", "哈希", "SHA256"
    };

    private static readonly Regex WindowsPathRegex = new(
        @"(?<![\p{L}\p{N}_])(?:(?:[A-Za-z]:[\\/])|(?:\\\\\?\\)|(?:\\\\))[^\r\n\t\""<>|，。；;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PosixPathRegex = new(
        @"(?<![:\w])/(?:[^/\s]+/)+[^\s，。；;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlRegex = new(
        @"\b(?:https?|wss?|file)://[^\s，。；;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecretRegex = new(
        @"\b(?:api[-_ ]?key|access[-_ ]?token|token|secret|password|passwd|authorization|bearer|connection\s*string)\b\s*[:=]?\s*[^\s，。；;]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmailRegex = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex IpAddressRegex = new(
        @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProcessIdentityRegex = new(
        @"\b(?:PID|HWND|ProcessId|Handle|Win32|HRESULT)\b\s*[:=#]?\s*(?:0x[0-9A-F]+|-?\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HexIdentifierRegex = new(
        @"\b0x[0-9A-F]{4,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoordinateTupleRegex = new(
        @"[\(\[]\s*-?\d{1,6}\s*,\s*-?\d{1,6}(?:\s*,\s*\d{1,6}\s*,\s*\d{1,6})?\s*[\)\]]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareCoordinateRegex = new(
        @"(?<![\d.])-?\d{1,6}\s*,\s*-?\d{1,6}(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PositionParameterRegex = new(
        @"\b(?:ClientX|ClientY|X|Y|Left|Top|Width|Height)\s*[:=]\s*-?\d+(?:\.\d+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DimensionRegex = new(
        @"\b\d{2,6}\s*[x×]\s*\d{2,6}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TimingRegex = new(
        @"\b\d+(?:\.\d+)?\s*(?:ms|us|µs|ns|ticks?|fps|hz)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HashRegex = new(
        @"\b(?:SHA(?:1|256|512)\s*[:=]?\s*)?[A-F0-9]{16,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InternalFileRegex = new(
        @"\b[\w.-]+\.(?:onnx|dll|exe|pdb|json|ya?ml|py|cs|xaml|config)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InternalProfileNameRegex = new(
        @"(?:^|[：:\s])(?:_app-state|[A-Z][A-Z0-9-]*-方案-[^\s，。；;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExceptionTypeRegex = new(
        @"\b(?:System\.)?[A-Z][A-Za-z0-9_.]*(?:Exception|Error)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InternalMetricRegex = new(
        @"\b(?:target|player|role|pet)?(?:hp|mp|score|confidence|threshold|latency)\s*[:=]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ContainsTechnicalDetails(string? module, string? message)
    {
        module ??= string.Empty;
        message ??= string.Empty;

        return TechnicalModuleTokens.Any(token => module.Contains(token, StringComparison.OrdinalIgnoreCase))
               || TechnicalMessageTokens.Any(token => message.Contains(token, StringComparison.OrdinalIgnoreCase))
               || WindowsPathRegex.IsMatch(message)
               || PosixPathRegex.IsMatch(message)
               || UrlRegex.IsMatch(message)
               || SecretRegex.IsMatch(message)
               || ProcessIdentityRegex.IsMatch(message)
               || HexIdentifierRegex.IsMatch(message)
               || CoordinateTupleRegex.IsMatch(message)
               || BareCoordinateRegex.IsMatch(message)
               || PositionParameterRegex.IsMatch(message)
               || DimensionRegex.IsMatch(message)
               || TimingRegex.IsMatch(message)
               || HashRegex.IsMatch(message)
               || InternalFileRegex.IsMatch(message)
               || InternalProfileNameRegex.IsMatch(message)
               || ExceptionTypeRegex.IsMatch(message)
               || InternalMetricRegex.IsMatch(message);
    }

    public static string CreateUserSummary(LogEntry entry, bool containsTechnicalDetails)
    {
        if (!string.IsNullOrWhiteSpace(entry.UserSummary))
        {
            var explicitSummary = SanitizeForDisplay(entry.UserSummary);
            if (!string.IsNullOrWhiteSpace(explicitSummary))
                return explicitSummary;
        }

        if (containsTechnicalDetails)
            return GenericUserSummary(entry.Module, entry.Level);

        var sanitized = SanitizeForDisplay(entry.Message);
        return string.IsNullOrWhiteSpace(sanitized)
            ? GenericUserSummary(entry.Module, entry.Level)
            : sanitized;
    }

    public static bool TryGetUserMessage(LogEntry entry, out string message)
    {
        message = string.Empty;
        if (entry.Audience != LogAudience.User || string.IsNullOrWhiteSpace(entry.UserSummary))
            return false;

        message = SanitizeForDisplay(entry.UserSummary);
        return !string.IsNullOrWhiteSpace(message);
    }

    public static string SanitizeForDisplay(string? value)
    {
        var cleaned = WhitespaceRegex.Replace(value ?? string.Empty, " ").Trim();
        if (cleaned.Length == 0)
            return string.Empty;

        cleaned = SecretRegex.Replace(cleaned, "[敏感信息]");
        cleaned = UrlRegex.Replace(cleaned, "[内部地址]");
        cleaned = EmailRegex.Replace(cleaned, "[账号信息]");
        cleaned = IpAddressRegex.Replace(cleaned, "[内部地址]");
        cleaned = WindowsPathRegex.Replace(cleaned, "[内部路径]");
        cleaned = PosixPathRegex.Replace(cleaned, "[内部路径]");
        cleaned = ProcessIdentityRegex.Replace(cleaned, "[内部标识]");
        cleaned = HexIdentifierRegex.Replace(cleaned, "[内部标识]");
        cleaned = CoordinateTupleRegex.Replace(cleaned, "[位置]");
        cleaned = BareCoordinateRegex.Replace(cleaned, "[位置]");
        cleaned = PositionParameterRegex.Replace(cleaned, "[位置]");
        cleaned = DimensionRegex.Replace(cleaned, "[界面状态]");
        cleaned = TimingRegex.Replace(cleaned, "[内部时序]");
        cleaned = HashRegex.Replace(cleaned, "[内部标识]");
        cleaned = InternalFileRegex.Replace(cleaned, "[内部文件]");
        cleaned = InternalProfileNameRegex.Replace(cleaned, " [内部配置]");
        cleaned = ExceptionTypeRegex.Replace(cleaned, "[内部异常]");

        foreach (var token in TechnicalMessageTokens)
            cleaned = cleaned.Replace(token, "[内部信息]", StringComparison.OrdinalIgnoreCase);

        cleaned = WhitespaceRegex.Replace(cleaned, " ").Trim();
        if (cleaned.Length > MaxUserMessageLength)
            cleaned = cleaned[..MaxUserMessageLength] + "…";

        return cleaned;
    }

    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Warn => "提醒",
        LogLevel.Error => "异常",
        _ => "信息"
    };

    private static string GenericUserSummary(string module, LogLevel level)
    {
        if (ContainsAny(module, "Ocr", "Vision", "Perception", "Recognition", "OpenText", "视觉", "识别", "模型"))
            return "画面识别暂未完成，程序会继续尝试。";

        if (ContainsAny(module, "Input", "Capture", "Window", "窗口", "截图", "输入"))
            return "当前操作未完成，请确认游戏窗口状态。";

        if (ContainsAny(module, "Profile", "Config", "配置", "方案"))
            return "配置操作未完成，请稍后重试。";

        if (ContainsAny(module, "Shout", "喊话"))
            return "喊话任务未完成，请稍后重试。";

        if (ContainsAny(module, "RoleSession", "会话", "角色"))
            return "当前角色任务未完成，请稍后重试。";

        return level switch
        {
            LogLevel.Error => "任务未完成，请稍后重试。",
            LogLevel.Warn => "当前操作未完成，请稍后重试。",
            _ => "运行状态已更新。"
        };
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}
