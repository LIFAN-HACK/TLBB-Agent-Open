using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class TargetPolicyCatalogService : ITargetPolicyCatalogService
{
    private readonly ILogService _log;

    public TargetPolicyCatalogService(ILogService log)
    {
        _log = log;
    }

    public IReadOnlyList<TargetPolicyEntry> Load(TargetPolicyCategoryKind category)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "data", "targets", FolderName(category));
        if (!Directory.Exists(folder))
            return Array.Empty<TargetPolicyEntry>();

        var entries = new Dictionary<string, TargetPolicyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                ReadFile(path, category, entries);
            }
            catch (Exception ex)
            {
                _log.Warn("目标数据", $"跳过无法读取的目标数据：{Path.GetFileName(path)}，{ex.Message}");
            }
        }

        return entries.Values
            .OrderBy(entry => entry.GroupPath, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public int MergeInto(UnifiedTargetPolicyConfig policy, TargetPolicyCategoryKind categoryKind)
    {
        policy.Categories ??= TargetPolicyDefaults.CreateCategories();
        policy.SuppressedBuiltInEntryIds ??= new List<string>();
        var suppressedBuiltInIds = new HashSet<string>(
            policy.SuppressedBuiltInEntryIds,
            StringComparer.OrdinalIgnoreCase);
        var category = policy.Categories.FirstOrDefault(item => item.Category == categoryKind);
        if (category is null)
        {
            category = new TargetPolicyCategoryConfig { Category = categoryKind };
            policy.Categories.Add(category);
        }

        category.Entries ??= new List<TargetPolicyEntry>();
        var added = 0;
        foreach (var imported in Load(categoryKind))
        {
            if (suppressedBuiltInIds.Contains(imported.Id))
                continue;

            var existing = category.Entries.FirstOrDefault(item =>
                string.Equals(item.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                category.Entries.Add(imported);
                added++;
                continue;
            }

            // Safety deny always wins; importing a normal allow entry never
            // turns a user or built-in deny back into an allow.
            if (!imported.AllowAttack)
                existing.AllowAttack = false;
            if (string.IsNullOrWhiteSpace(existing.GroupPath))
                existing.GroupPath = imported.GroupPath;
            if (string.IsNullOrWhiteSpace(existing.Source))
                existing.Source = imported.Source;
            existing.BuiltIn |= imported.BuiltIn;
        }

        return added;
    }

    private static void ReadFile(
        string path,
        TargetPolicyCategoryKind requestedCategory,
        IDictionary<string, TargetPolicyEntry> entries)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("entries", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        var source = StringProperty(root, "source");
        var defaultGroup = StringProperty(root, "group");
        foreach (var item in items.EnumerateArray())
        {
            var name = StringProperty(item, "name").Trim();
            if (name.Length == 0)
                continue;

            var allowAttack = !item.TryGetProperty("allowAttack", out var allowElement)
                              || allowElement.ValueKind != JsonValueKind.False;
            var group = StringProperty(item, "group");
            if (group.Length == 0)
                group = defaultGroup;
            var reason = StringProperty(item, "reason");

            var imported = new TargetPolicyEntry
            {
                Id = StableId(requestedCategory, name),
                Name = name,
                Enabled = true,
                AllowAttack = allowAttack,
                BuiltIn = true,
                GroupPath = group,
                Source = source.Length == 0 ? path : source,
                Notes = reason
            };

            if (!entries.TryGetValue(name, out var existing))
            {
                entries[name] = imported;
                continue;
            }

            if (!imported.AllowAttack)
                existing.AllowAttack = false;
            if (existing.GroupPath.Length == 0)
                existing.GroupPath = imported.GroupPath;
            if (existing.Source.Length == 0)
                existing.Source = imported.Source;
        }
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string StableId(TargetPolicyCategoryKind category, string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{category}:{name}"));
        return $"catalog-{Convert.ToHexString(bytes)[..12].ToLowerInvariant()}";
    }

    private static string FolderName(TargetPolicyCategoryKind category) => category switch
    {
        TargetPolicyCategoryKind.Boss => "boss",
        TargetPolicyCategoryKind.Player => "player",
        _ => "monster"
    };
}
