using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ItemPolicyService : IItemPolicyService
{
    private const string Module = "ItemPolicy";
    private static readonly string[] SafetyBlockKeywords =
    {
        "\u7ed1\u5b9a",
        "\u4efb\u52a1",
        "\u8d35\u91cd",
        "\u73cd\u517d",
        "\u5b9d\u77f3",
        "\u88c5\u5907",
        "\u79d8\u7c4d",
        "\u6280\u80fd\u4e66",
        "\u5143\u5b9d"
    };

    private readonly ILogService _log;

    static ItemPolicyService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ItemPolicyService(ILogService log)
    {
        _log = log;
    }

    public ItemCleanupDecision Decide(string itemName, ItemPolicyConfig config, out string reason)
    {
        Normalize(config);
        var name = NormalizeName(itemName);
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = "未识别物品名称，按未知物品保护。";
            return config.ProtectUnknownItems ? ItemCleanupDecision.BlockedUnknown : ItemCleanupDecision.Keep;
        }

        if (Contains(config.BlockKeywords, name))
        {
            reason = "命中禁止处理关键词。";
            return ItemCleanupDecision.Keep;
        }

        if (Contains(config.KeepItems, name))
        {
            reason = "命中保留清单。";
            return ItemCleanupDecision.Keep;
        }

        if (Contains(config.StoreItems, name))
        {
            reason = "命中存放清单。";
            return ItemCleanupDecision.Store;
        }

        if (Contains(config.TradeItems, name))
        {
            reason = "命中交易清单。";
            return ItemCleanupDecision.Trade;
        }

        if (ContainsExact(config.AllowedSellItems, name))
        {
            reason = "命中精确出售许可名单，仅生成出售候选。";
            return ItemCleanupDecision.SellCandidate;
        }

        if (Contains(config.SellKeywords, name))
        {
            reason = "命中明确出售关键词。";
            return ItemCleanupDecision.SellCandidate;
        }

        reason = config.RequireExplicitSellKeyword
            ? "未命中明确出售关键词，按保护处理。"
            : "未命中保护清单，可作为候选。";
        return config.RequireExplicitSellKeyword ? ItemCleanupDecision.BlockedUnknown : ItemCleanupDecision.SellCandidate;
    }

    public int LoadLegacyItemLists(string sourceRoot, ItemPolicyConfig config)
    {
        Normalize(config);
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
        {
            _log.Warn(Module, $"旧物品清单目录不存在：{root}");
            return 0;
        }

        var before = CountAll(config);
        foreach (var file in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("保留物品", StringComparison.Ordinal))
                AddLines(config.KeepItems, file);
            else if (name.Contains("存放物品", StringComparison.Ordinal))
                AddLines(config.StoreItems, file);
            else if (name.Contains("交易物品", StringComparison.Ordinal))
                AddLines(config.TradeItems, file);
        }

        AddLegacyBagTemplateNames(config.KeepItems, root);
        Deduplicate(config);
        var loaded = CountAll(config) - before;
        _log.Info(Module, $"旧物品清单已导入：新增 {loaded} 条。");
        return loaded;
    }

    private static void Normalize(ItemPolicyConfig config)
    {
        config.AllowedSellItems ??= new List<string>();
        config.KeepItems ??= new List<string>();
        config.StoreItems ??= new List<string>();
        config.TradeItems ??= new List<string>();
        config.SellKeywords ??= new List<string>();
        config.BlockKeywords ??= new List<string>();

        foreach (var keyword in SafetyBlockKeywords)
        {
            if (!config.BlockKeywords.Any(value => NormalizeName(value).Equals(keyword, StringComparison.OrdinalIgnoreCase)))
                config.BlockKeywords.Add(keyword);
        }
    }

    private static int CountAll(ItemPolicyConfig config) =>
        config.AllowedSellItems.Count + config.KeepItems.Count + config.StoreItems.Count + config.TradeItems.Count + config.SellKeywords.Count + config.BlockKeywords.Count;

    private static void AddLines(List<string> target, string path)
    {
        foreach (var raw in ReadLines(path))
        {
            var value = NormalizeName(raw);
            if (value.Length > 0)
                target.Add(value);
        }
    }

    private static void AddLegacyBagTemplateNames(List<string> target, string root)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".bmp",
            ".png",
            ".jpg",
            ".jpeg"
        };

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file)))
                continue;

            var value = TryNormalizeLegacyBagTemplateName(file);
            if (value.Length > 0)
                target.Add(value);
        }
    }

    private static string TryNormalizeLegacyBagTemplateName(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var fullPath = path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        var hasBagMarker =
            fileName.StartsWith("背包_", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains("/背包/", StringComparison.OrdinalIgnoreCase);

        if (!hasBagMarker)
            return string.Empty;

        var name = NormalizeName(fileName);
        return IsGenericLegacyTemplateName(name) ? string.Empty : name;
    }

    private static bool IsGenericLegacyTemplateName(string name)
    {
        if (name.Length == 0)
            return true;

        if (name.All(char.IsDigit))
            return true;

        return name.Equals("背包", StringComparison.OrdinalIgnoreCase)
               || name.Equals("格子", StringComparison.OrdinalIgnoreCase)
               || name.Equals("空格", StringComparison.OrdinalIgnoreCase)
               || name.Equals("物品", StringComparison.OrdinalIgnoreCase)
               || name.Equals("道具", StringComparison.OrdinalIgnoreCase)
               || name.Equals("材料", StringComparison.OrdinalIgnoreCase)
               || name.Equals("整理", StringComparison.OrdinalIgnoreCase)
               || name.Equals("锁定", StringComparison.OrdinalIgnoreCase)
               || name.Equals("拆分", StringComparison.OrdinalIgnoreCase);
    }

    private static void Deduplicate(ItemPolicyConfig config)
    {
        config.AllowedSellItems = Distinct(config.AllowedSellItems);
        config.KeepItems = Distinct(config.KeepItems);
        config.StoreItems = Distinct(config.StoreItems);
        config.TradeItems = Distinct(config.TradeItems);
        config.SellKeywords = Distinct(config.SellKeywords);
        config.BlockKeywords = Distinct(config.BlockKeywords);
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Select(NormalizeName)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Contains(IEnumerable<string> patterns, string itemName) =>
        patterns.Select(NormalizeName)
            .Where(p => p.Length > 0)
            .Any(p => itemName.Contains(p, StringComparison.OrdinalIgnoreCase)
                      || p.Contains(itemName, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsExact(IEnumerable<string> names, string itemName) =>
        names.Select(NormalizeName)
            .Any(name => name.Equals(itemName, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeName(string value)
    {
        var text = value.Trim()
            .Trim('\uFEFF')
            .Replace("背包_", "", StringComparison.Ordinal)
            .Replace("[勾]", "", StringComparison.Ordinal)
            .Replace("[类]", "", StringComparison.Ordinal)
            .Replace("[保留]", "", StringComparison.Ordinal)
            .Replace("[存放]", "", StringComparison.Ordinal)
            .Replace("[交易]", "", StringComparison.Ordinal);

        if (text.StartsWith('#') || text.StartsWith(';') || text.StartsWith("//", StringComparison.Ordinal))
            return string.Empty;

        var equals = text.IndexOf('=');
        if (equals >= 0 && equals + 1 < text.Length)
            text = text[(equals + 1)..];

        return text.Trim();
    }

    private static string[] ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return SplitLines(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
