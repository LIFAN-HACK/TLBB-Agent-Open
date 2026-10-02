namespace TlbbAgent.Contracts;

public sealed class RuntimeDataIndexSummary
{
    public bool Exists { get; init; }
    public string Status { get; init; } = "";
    public string UpdatedAt { get; init; } = "-";
    public int Total { get; init; }
    public int Available { get; init; }
    public int Skipped { get; init; }
    public int Missing { get; init; }
    public IReadOnlyList<LegacyImportIndexItem> Items { get; init; } = Array.Empty<LegacyImportIndexItem>();
}

public interface IRuntimeDataService
{
    string? FindRoot();
    bool TryResolveFile(string relativePath, out string path);
    bool TryResolveDirectory(string relativePath, out string path);
    IReadOnlyList<string> EnumerateFiles(string relativeDirectory, string searchPattern, bool recursive);
    IReadOnlyList<string> EnumerateDictionaryFiles();
    /// <summary>
    /// UU number packs only for hang combat HP digit confirm.
    /// Excludes core-aj / map / zk (too large; freezes every-frame OCR).
    /// </summary>
    IReadOnlyList<string> EnumerateCombatDictionaryFiles();
    RuntimeDataIndexSummary ReadPublicIndex();
}
