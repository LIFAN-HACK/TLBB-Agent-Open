namespace TlbbAgent.Contracts;

public enum CaptchaKind
{
    TextClick = 0,
    AnyClick,
    SliderGap,
    Jigsaw,
    Rotate,
    CharNumeric,
    CharChinese
}

public sealed record CaptchaPoint(int X, int Y);

public sealed record CaptchaSolveResult(
    bool Success,
    string Raw,
    IReadOnlyList<CaptchaPoint> Points,
    int? SliderDistance,
    int? RotateAngle,
    string Message,
    string? UniqueCode)
{
    public static CaptchaSolveResult Fail(string message) =>
        new(false, string.Empty, Array.Empty<CaptchaPoint>(), null, null, message, null);
}

public interface ICaptchaSolver
{
    bool IsConfigured { get; }

    Task<CaptchaSolveResult> SolveAsync(
        CaptchaKind kind, CaptureResult image, CaptureResult? slidePiece = null, CancellationToken ct = default);

    Task<decimal> QueryBalanceAsync(CancellationToken ct = default);

    Task ReportErrorAsync(string uniqueCode, CancellationToken ct = default);
}

public sealed class CaptchaConfig
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "yunma";
    public string Token { get; set; } = "";
    public string ApiBase { get; set; } = "http://api.jfbym.com";
    public int TimeoutMs { get; set; } = 15000;
    public int MaxRetries { get; set; } = 2;
}
