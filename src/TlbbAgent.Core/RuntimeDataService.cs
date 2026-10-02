using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RuntimeDataService(ILogService log) : IRuntimeDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string? FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "data", "imported");
                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }
        }

        return null;
    }

    public bool TryResolveFile(string relativePath, out string path)
    {
        path = "";
        var root = FindRoot();
        if (root is null)
            return false;

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInside(root, candidate) || !File.Exists(candidate))
            return false;

        path = candidate;
        return true;
    }

    public bool TryResolveDirectory(string relativePath, out string path)
    {
        path = "";
        var root = FindRoot();
        if (root is null)
            return false;

        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInside(root, candidate) || !Directory.Exists(candidate))
            return false;

        path = candidate;
        return true;
    }

    public IReadOnlyList<string> EnumerateDictionaryFiles()
    {
        var root = FindRoot();
        if (root is null)
            return Array.Empty<string>();

        return Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}ocr{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        || p.Contains($"{Path.DirectorySeparatorChar}core{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> EnumerateCombatDictionaryFiles()
    {
        var root = FindRoot();
        if (root is null)
            return Array.Empty<string>();

        var results = new List<string>();
        var ocrDir = Path.Combine(root, "ocr");
        if (!Directory.Exists(ocrDir))
            return results;

        // Hang combat only needs digit packs. core-aj / map-001 / zk are huge and
        // make every-frame status OCR freeze the loop (seconds per scan).
        foreach (var file in Directory.EnumerateFiles(ocrDir, "*.txt", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("number", StringComparison.OrdinalIgnoreCase))
                results.Add(file);
        }

        return results;
    }

    public IReadOnlyList<string> EnumerateFiles(string relativeDirectory, string searchPattern, bool recursive)
    {
        if (!TryResolveDirectory(relativeDirectory, out var directory))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(
                directory,
                string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public RuntimeDataIndexSummary ReadPublicIndex()
    {
        var root = FindRoot();
        if (root is null)
            return new RuntimeDataIndexSummary { Status = "未找到数据目录" };

        var indexPath = Path.Combine(root, "index.json");
        if (!File.Exists(indexPath))
            return new RuntimeDataIndexSummary { Status = "未生成索引" };

        try
        {
            var items = JsonSerializer.Deserialize<List<LegacyImportIndexItem>>(File.ReadAllText(indexPath), JsonOptions) ?? new();
            var available = items.Count(i => i.Status.Equals("available", StringComparison.OrdinalIgnoreCase));
            var skipped = items.Count(i => i.Status.Equals("skipped", StringComparison.OrdinalIgnoreCase));
            var missing = items.Count(i => i.Status.Equals("missing", StringComparison.OrdinalIgnoreCase));

            return new RuntimeDataIndexSummary
            {
                Exists = true,
                Status = items.Count == 0 ? "索引为空" : $"可用 {available} / {items.Count}",
                UpdatedAt = File.GetLastWriteTime(indexPath).ToString("yyyy-MM-dd HH:mm"),
                Total = items.Count,
                Available = available,
                Skipped = skipped,
                Missing = missing,
                Items = items
            };
        }
        catch
        {
            log.Warn("运行数据", "索引读取失败。");
            return new RuntimeDataIndexSummary { Status = "索引读取失败" };
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
