using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TlbbAgent.App;

internal static class RuntimeBuildIdentity
{
    private const string ManifestFileName = "runtime-manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string[] RequiredFiles =
    [
        "TLBB Agent.exe",
        "TLBB Agent.dll",
        "TlbbAgent.Core.dll",
        "TlbbAgent.Contracts.dll"
    ];

    public static string ValidateCurrentBuild()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var manifestPath = Path.Combine(baseDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("当前程序不是受控发布版本，请使用统一启动入口。");

        RuntimeManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RuntimeManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidOperationException("运行版本清单损坏，请重新发布当前版本。", ex);
        }

        if (manifest is null || manifest.SchemaVersion != 1 || manifest.Files is null)
            throw new InvalidOperationException("运行版本清单无效，请重新发布当前版本。");

        foreach (var fileName in RequiredFiles)
        {
            if (!manifest.Files.TryGetValue(fileName, out var expectedHash)
                || string.IsNullOrWhiteSpace(expectedHash))
            {
                throw new InvalidOperationException($"运行版本清单缺少 {fileName}。");
            }

            var path = Path.Combine(baseDirectory, fileName);
            if (!File.Exists(path))
                throw new InvalidOperationException($"运行组件缺失：{fileName}。");

            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"运行组件版本不一致：{fileName}。请使用统一启动入口重新发布。");
            }
        }

        return string.IsNullOrWhiteSpace(manifest.BuildFingerprint)
            ? "UNKNOWN"
            : manifest.BuildFingerprint.Trim().ToUpperInvariant();
    }

    private sealed class RuntimeManifest
    {
        public int SchemaVersion { get; set; }
        public string BuildFingerprint { get; set; } = string.Empty;
        public Dictionary<string, string>? Files { get; set; }
    }
}
