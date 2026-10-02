using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class BagItemTemplateMatchService : IBagItemTemplateMatchService
{
    private const string Module = "BagTemplate";
    private readonly IInventoryRecognitionService _inventory;
    private readonly ILogService _log;

    public BagItemTemplateMatchService(IInventoryRecognitionService inventory, ILogService log)
    {
        _inventory = inventory;
        _log = log;
    }

    public BagItemTemplateApplyResult Apply(
        CaptureResult capture,
        InventoryGridConfig grid,
        ItemPolicyConfig policy,
        BagItemTemplateMatchConfig config)
    {
        Normalize(config);
        if (!config.Enabled)
            return BagItemTemplateApplyResult.Disabled;

        if (string.IsNullOrWhiteSpace(config.IndexPath) || !File.Exists(config.IndexPath))
        {
            var message = "模板索引不存在，本次清包按未知物品保护。";
            _log.Warn(Module, message);
            return new BagItemTemplateApplyResult(
                true,
                false,
                message,
                0,
                0,
                0,
                0,
                Array.Empty<BagItemTemplateSlotMatch>());
        }

        var templates = LoadTemplates(config.IndexPath);
        if (templates.Count == 0)
        {
            var message = "模板索引没有可用模板，本次清包按未知物品保护。";
            _log.Warn(Module, message);
            return new BagItemTemplateApplyResult(
                true,
                false,
                message,
                0,
                0,
                0,
                0,
                Array.Empty<BagItemTemplateSlotMatch>());
        }

        var scan = _inventory.Scan(capture, CloneGrid(grid));
        if (!scan.Success)
        {
            _log.Warn(Module, scan.Message);
            return new BagItemTemplateApplyResult(
                true,
                false,
                scan.Message,
                templates.Count,
                0,
                0,
                0,
                Array.Empty<BagItemTemplateSlotMatch>());
        }

        var rows = new List<BagItemTemplateSlotMatch>();
        foreach (var slot in scan.Slots.Where(s => s.State == InventorySlotState.Occupied).OrderBy(s => s.Index))
        {
            var icon = CropCaptureRect(
                capture,
                slot.X + config.InnerPadding,
                slot.Y + config.InnerPadding,
                slot.Width - config.InnerPadding * 2,
                slot.Height - config.InnerPadding * 2);

            var hash = icon is null ? "" : ComputeAverageHash64(icon);
            var best = string.IsNullOrWhiteSpace(hash) ? null : FindBest(hash, templates);
            var matched = best is not null && best.Value.Score >= config.Threshold;
            var itemName = matched ? best!.Value.Template.ItemName : "";
            var policyName = matched ? NormalizePolicy(best!.Value.Template.Policy) : "UnknownProtected";
            var applied = false;
            var reason = "未命中可信模板，按未知物品保护。";

            if (matched)
            {
                if (grid.SlotNameOverrides.ContainsKey(slot.Index))
                {
                    reason = "命中模板，但该格已有手工标注，保留手工标注。";
                }
                else if (policyName is "Ignore" or "UnknownProtected")
                {
                    reason = $"命中模板 {itemName}，策略 {policyName}，不写入候选处理策略。";
                }
                else
                {
                    grid.SlotNameOverrides[slot.Index] = itemName;
                    grid.SlotNameSources[slot.Index] = "template-hash-match";
                    ApplyPolicy(itemName, policyName, policy);
                    applied = true;
                    reason = $"命中模板 {itemName}，策略 {policyName}，已用于本次候选。";
                }
            }

            rows.Add(new BagItemTemplateSlotMatch(
                slot.Index,
                slot.Row,
                slot.Column,
                matched,
                applied,
                matched ? best!.Value.Score : 0,
                itemName,
                policyName,
                matched ? best!.Value.Template.Id : "",
                reason));
        }

        var result = new BagItemTemplateApplyResult(
            true,
            true,
            $"模板匹配完成：模板 {templates.Count}，占用格 {rows.Count}，命中 {rows.Count(r => r.Matched)}，应用 {rows.Count(r => r.AppliedToCleanup)}。",
            templates.Count,
            rows.Count,
            rows.Count(r => r.Matched),
            rows.Count(r => r.AppliedToCleanup),
            rows);

        _log.Info(Module, result.Message);
        return result;
    }

    private List<BagItemTemplateEntry> LoadTemplates(string indexPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(indexPath, Encoding.UTF8))?.AsObject();
        if (root is null
            || !string.Equals(root["libraryId"]?.GetValue<string>(), "item_assets_v1", StringComparison.Ordinal)
            || root["runtimeSellAuthorized"]?.GetValue<bool>() != false
            || !string.Equals(root["safetyDefault"]?.GetValue<string>(), "unknown_protected", StringComparison.Ordinal))
        {
            _log.Warn(Module, "物品模板索引安全契约无效，全部物品按未知保护。");
            return new List<BagItemTemplateEntry>();
        }

        var templates = root?["templates"]?.AsArray();
        if (templates is null)
            return new List<BagItemTemplateEntry>();

        var result = new List<BagItemTemplateEntry>();
        var indexDirectory = Path.GetDirectoryName(Path.GetFullPath(indexPath)) ?? "";
        var allowedPrefix = indexDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        foreach (var node in templates.OfType<JsonObject>())
        {
            var id = node["id"]?.GetValue<string>()?.Trim() ?? "";
            var itemName = node["itemName"]?.GetValue<string>()?.Trim() ?? "";
            var policy = NormalizePolicy(node["policy"]?.GetValue<string>() ?? "UnknownProtected");
            var image = node["image"]?.AsObject();
            var relativePath = image?["path"]?.GetValue<string>()?.Trim() ?? "";
            var expectedSha256 = image?["sha256"]?.GetValue<string>()?.Trim() ?? "";
            var hash = image?["averageHash"]?.GetValue<string>()?.Trim() ?? "";

            if (id.Length == 0
                || itemName.Length == 0
                || hash.Length == 0
                || policy != "Keep"
                || relativePath.Length == 0
                || expectedSha256.Length != 64)
                continue;

            var imagePath = Path.GetFullPath(Path.Combine(indexDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!imagePath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(imagePath)
                || !ComputeSha256(imagePath).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new BagItemTemplateEntry(id, itemName, policy, hash, imagePath, expectedSha256));
        }

        if (result.Count != templates.Count)
            _log.Warn(Module, $"物品保护索引仅通过 {result.Count}/{templates.Count} 个模板；未通过项继续按未知保护。");

        return result;
    }

    private static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static (BagItemTemplateEntry Template, double Score)? FindBest(string hash, IReadOnlyList<BagItemTemplateEntry> templates)
    {
        var best = default(BagItemTemplateEntry);
        var bestScore = -1.0;
        foreach (var template in templates)
        {
            var score = CompareHashSimilarity(hash, template.AverageHash);
            if (score > bestScore)
            {
                best = template;
                bestScore = score;
            }
        }

        return best is null ? null : (best, bestScore);
    }

    private static double CompareHashSimilarity(string left, string right)
    {
        if (!ulong.TryParse(left, System.Globalization.NumberStyles.HexNumber, null, out var a)
            || !ulong.TryParse(right, System.Globalization.NumberStyles.HexNumber, null, out var b))
            return 0.0;

        var distance = System.Numerics.BitOperations.PopCount(a ^ b);
        return Math.Clamp(1.0 - distance / 64.0, 0.0, 1.0);
    }

    private static string ComputeAverageHash64(CaptureResult image)
    {
        const int size = 8;
        Span<double> values = stackalloc double[size * size];
        for (var y = 0; y < size; y++)
        {
            var sourceY = Math.Clamp((int)Math.Round((y + 0.5) * image.Height / size), 0, Math.Max(0, image.Height - 1));
            for (var x = 0; x < size; x++)
            {
                var sourceX = Math.Clamp((int)Math.Round((x + 0.5) * image.Width / size), 0, Math.Max(0, image.Width - 1));
                var p = (sourceY * image.Width + sourceX) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                values[y * size + x] = r * 0.299 + g * 0.587 + b * 0.114;
            }
        }

        var avg = 0.0;
        for (var i = 0; i < values.Length; i++)
            avg += values[i];
        avg /= values.Length;

        ulong bits = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] >= avg)
                bits |= 1UL << i;
        }

        return bits.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CaptureResult? CropCaptureRect(CaptureResult source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var left = Math.Clamp(x, 0, Math.Max(0, source.Width - 1));
        var top = Math.Clamp(y, 0, Math.Max(0, source.Height - 1));
        var right = Math.Clamp(x + width, left + 1, source.Width);
        var bottom = Math.Clamp(y + height, top + 1, source.Height);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
            return null;

        var dst = new byte[cropWidth * cropHeight * 4];
        var srcStride = source.Width * 4;
        var dstStride = cropWidth * 4;
        for (var row = 0; row < cropHeight; row++)
            Array.Copy(source.Pixels, (top + row) * srcStride + left * 4, dst, row * dstStride, dstStride);

        return new CaptureResult(cropWidth, cropHeight, dst);
    }

    private static void ApplyPolicy(string itemName, string policyName, ItemPolicyConfig policy)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return;

        static void AddUnique(List<string> list, string value)
        {
            var normalized = value.Trim();
            if (!list.Any(v => string.Equals(v.Trim(), normalized, StringComparison.OrdinalIgnoreCase)))
                list.Add(normalized);
        }

        switch (NormalizePolicy(policyName))
        {
            case "Keep":
                AddUnique(policy.KeepItems, itemName);
                break;
            case "Store":
                AddUnique(policy.StoreItems, itemName);
                break;
            case "Trade":
                AddUnique(policy.TradeItems, itemName);
                break;
            case "SellCandidate":
                AddUnique(policy.SellKeywords, itemName);
                break;
        }
    }

    private static string NormalizePolicy(string policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
            return "UnknownProtected";

        return policy.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase) switch
        {
            "Keep" => "Keep",
            "Store" => "Store",
            "Trade" => "Trade",
            "SellCandidate" => "SellCandidate",
            "BlockedUnknown" => "UnknownProtected",
            "UnknownProtected" => "UnknownProtected",
            "Ignore" => "Ignore",
            _ => "UnknownProtected"
        };
    }

    private static void Normalize(BagItemTemplateMatchConfig config)
    {
        config.IndexPath = config.IndexPath?.Trim() ?? "";
        config.Threshold = Math.Clamp(config.Threshold, 0.75, 1.0);
        config.InnerPadding = Math.Clamp(config.InnerPadding, 0, 8);
    }

    private static InventoryGridConfig CloneGrid(InventoryGridConfig source) =>
        new()
        {
            ScanMode = source.ScanMode,
            Enabled = source.Enabled,
            X = source.X,
            Y = source.Y,
            Columns = source.Columns,
            Rows = source.Rows,
            CellWidth = source.CellWidth,
            CellHeight = source.CellHeight,
            GapX = source.GapX,
            GapY = source.GapY,
            SlotNameOverrides = new Dictionary<int, string>(source.SlotNameOverrides),
            SlotNameSources = new Dictionary<int, string>(source.SlotNameSources)
        };

    private sealed record BagItemTemplateEntry(
        string Id,
        string ItemName,
        string Policy,
        string AverageHash,
        string ImagePath,
        string Sha256);
}
