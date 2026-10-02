using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Captcha;

public sealed class YunmaCaptchaSolver(ILogService log, IGlobalConfigService config) : ICaptchaSolver
{
    private const string Module = "Captcha";
    private static readonly HttpClient Http = new();

    private static string TypeCode(CaptchaKind kind) => kind switch
    {
        CaptchaKind.TextClick   => "30100",
        CaptchaKind.AnyClick    => "30009",
        CaptchaKind.SliderGap   => "20111",
        CaptchaKind.Jigsaw      => "20111",
        CaptchaKind.Rotate      => "90007",
        CaptchaKind.CharNumeric => "10110",
        CaptchaKind.CharChinese => "10115",
        _ => "30100"
    };

    private CaptchaConfig Cfg => config.Current.Captcha;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Cfg.Token);

    public async Task<CaptchaSolveResult> SolveAsync(
        CaptchaKind kind, CaptureResult image, CaptureResult? slidePiece = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            log.Warn(Module, "未配置打码 token，跳过识别。");
            return CaptchaSolveResult.Fail("未配置 token");
        }

        var payload = new Dictionary<string, string>
        {
            ["token"] = Cfg.Token,
            ["type"] = TypeCode(kind),
            ["image"] = PngEncoder.ToBase64(image.Width, image.Height, image.Pixels)
        };
        if (slidePiece is not null)
            payload["slide_image"] = PngEncoder.ToBase64(slidePiece.Width, slidePiece.Height, slidePiece.Pixels);

        string url = $"{Cfg.ApiBase}/api/YmServer/customApi";
        int attempts = Math.Max(1, Cfg.MaxRetries + 1);

        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Cfg.TimeoutMs);

                using var resp = await Http.PostAsJsonAsync(url, payload, cts.Token).ConfigureAwait(false);
                var doc = await JsonDocument.ParseAsync(
                    await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token)
                    .ConfigureAwait(false);

                var root = doc.RootElement;
                int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
                string msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";

                if (code != 10000)
                {
                    log.Warn(Module, $"识别失败(code={code}): {msg}");
                    return CaptchaSolveResult.Fail($"code={code} {msg}");
                }

                var data = root.GetProperty("data");
                string raw = data.TryGetProperty("data", out var d) ? (d.GetString() ?? "") : "";
                string? unique = data.TryGetProperty("unique_code", out var u) ? u.GetString() : null;

                var result = Parse(kind, raw, msg, unique);
                log.Info(Module, "识别成功。");
                return result;
            }
            catch (Exception) when (i < attempts)
            {
                log.Warn(Module, "识别异常，重试。");
            }
            catch
            {
                log.Error(Module, "识别失败。");
                return CaptchaSolveResult.Fail("识别失败");
            }
        }
        return CaptchaSolveResult.Fail("重试耗尽");
    }

    public async Task<decimal> QueryBalanceAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return 0m;
        try
        {
            var payload = new Dictionary<string, string> { ["token"] = Cfg.Token, ["type"] = "score" };
            string url = $"{Cfg.ApiBase}/api/YmServer/getUserInfoApi";
            using var resp = await Http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("score", out var s))
            {
                var str = s.ValueKind == JsonValueKind.String ? s.GetString() : s.GetRawText();
                return decimal.TryParse(str, out var v) ? v : 0m;
            }
        }
        catch
        {
            log.Warn(Module, "查询余额失败。");
        }
        return 0m;
    }

    public async Task ReportErrorAsync(string uniqueCode, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(uniqueCode)) return;
        try
        {
            var payload = new Dictionary<string, string> { ["token"] = Cfg.Token, ["unique_code"] = uniqueCode };
            string url = $"{Cfg.ApiBase}/api/YmServer/refundApi";
            using var resp = await Http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
            log.Info(Module, "已上报识别错误。");
        }
        catch
        {
            log.Warn(Module, "上报识别错误失败。");
        }
    }

    private static CaptchaSolveResult Parse(CaptchaKind kind, string raw, string msg, string? unique)
    {
        switch (kind)
        {
            case CaptchaKind.SliderGap:
            case CaptchaKind.Jigsaw:
            {
                int? dist = FirstInt(raw);
                return new CaptchaSolveResult(dist is not null, raw, Array.Empty<CaptchaPoint>(), dist, null, msg, unique);
            }
            case CaptchaKind.Rotate:
            {
                int? angle = FirstInt(raw);
                return new CaptchaSolveResult(angle is not null, raw, Array.Empty<CaptchaPoint>(), null, angle, msg, unique);
            }
            case CaptchaKind.CharNumeric:
            case CaptchaKind.CharChinese:
                return new CaptchaSolveResult(raw.Length > 0, raw, Array.Empty<CaptchaPoint>(), null, null, msg, unique);
            default:
            {
                var pts = ParsePoints(raw);
                return new CaptchaSolveResult(pts.Count > 0, raw, pts, null, null, msg, unique);
            }
        }
    }

    private static List<CaptchaPoint> ParsePoints(string raw)
    {
        var pts = new List<CaptchaPoint>();
        if (string.IsNullOrWhiteSpace(raw)) return pts;
        foreach (var seg in raw.Split(new[] { '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = seg.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length >= 2 &&
                int.TryParse(xy[0].Trim(), out var x) &&
                int.TryParse(xy[1].Trim(), out var y))
                pts.Add(new CaptchaPoint(x, y));
        }
        return pts;
    }

    private static int? FirstInt(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return first.Length > 0 && int.TryParse(first[0].Trim(), out var v) ? v : null;
    }
}
