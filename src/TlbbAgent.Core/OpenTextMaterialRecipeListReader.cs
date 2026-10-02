using System.Security.Cryptography;
using System.Text;
using TlbbAgent.Contracts;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.Core;

public sealed class OpenTextMaterialRecipeListReader : IMaterialRecipeListReader
{
    private const int VisibleRowCount = 5;
    private const double MinimumConfidence = 0.62;
    private const double StrongConfidence = 0.78;
    private readonly IOpenTextRecognizer _openText;

    public OpenTextMaterialRecipeListReader(IOpenTextRecognizer openText)
    {
        _openText = openText;
    }

    public MaterialRecipePage Read(CaptureResult panelCrop, VisionBox panelBox)
    {
        if (!_openText.IsAvailable)
            return MaterialRecipePage.Unavailable("L2 文本识别不可用。");

        var region = ResolveRecipeRegion(panelCrop.Width, panelCrop.Height);
        if (region.IsEmpty)
            return MaterialRecipePage.Unavailable("配方列表区域无效。");

        var rowHeight = Math.Max(1, region.Height / VisibleRowCount);
        var rows = new List<MaterialRecipeRow>();
        for (var index = 0; index < VisibleRowCount; index++)
        {
            var y = region.Y + index * rowHeight;
            var height = index == VisibleRowCount - 1 ? region.Bottom - y : rowHeight;
            var localBox = new VisionBox(region.X, y, region.Width, height);
            var candidate = RecognizeRow(panelCrop, localBox);
            if (candidate is null)
                continue;

            rows.Add(new MaterialRecipeRow(
                candidate.Value.Name,
                new VisionBox(panelBox.X + localBox.X, panelBox.Y + localBox.Y, localBox.Width, localBox.Height),
                candidate.Value.Result.Confidence,
                candidate.Value.Result.Text));
        }

        var uniqueRows = rows
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => row.Confidence).First())
            .ToArray();
        // Processing every recipe must fail closed. A missed non-empty row would otherwise be
        // silently skipped and the whole page could be reported as exhausted.
        if (uniqueRows.Length != VisibleRowCount)
            return MaterialRecipePage.Unavailable($"配方页未完整识别：{uniqueRows.Length}/{VisibleRowCount}，禁止继续加工。");

        var fingerprintSource = string.Join('\n', uniqueRows.Select(row => row.Name));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)))[..16];
        return new MaterialRecipePage(
            true,
            uniqueRows,
            fingerprint,
            $"识别到 {uniqueRows.Length} 个唯一配方。" );
    }

    private (string Name, OpenTextResult Result)? RecognizeRow(CaptureResult panelCrop, VisionBox rowBox)
    {
        var variants = new[]
        {
            rowBox,
            VisionBox.FromXyxy(rowBox.X, rowBox.Y - 3, rowBox.Right, rowBox.Bottom + 3),
            VisionBox.FromXyxy(rowBox.X, rowBox.Y - 2, rowBox.Right, rowBox.Bottom + 1),
            VisionBox.FromXyxy(rowBox.X, rowBox.Y + 1, rowBox.Right, rowBox.Bottom + 3)
        };

        (string Name, OpenTextResult Result)? best = null;
        foreach (var variant in variants)
        {
            var crop = OpenTextCropUtil.Crop(panelCrop, variant, pad: 0);
            if (crop is null)
                continue;

            var result = _openText.Recognize(crop, OpenTextSlot.ItemName);
            var name = NormalizeRecipeName(result.Text);
            if (result.Confidence < MinimumConfidence || string.IsNullOrWhiteSpace(name))
                continue;

            if (best is null || result.Confidence > best.Value.Result.Confidence)
                best = (name, result);
            if (result.Confidence >= StrongConfidence)
                break;
        }

        return best;
    }

    private static VisionBox ResolveRecipeRegion(int width, int height)
    {
        var x1 = (int)Math.Round(width * 0.105740);
        var y1 = (int)Math.Round(height * 0.178082);
        var x2 = (int)Math.Round(width * 0.882175);
        var y2 = (int)Math.Round(height * 0.463470);
        return VisionBox.FromXyxy(x1, y1, x2, y2);
    }

    private static string NormalizeRecipeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var compact = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
        var recipeEnd = compact.LastIndexOf("配方", StringComparison.Ordinal);
        if (recipeEnd < 0)
            return string.Empty;

        compact = compact[..(recipeEnd + 2)];
        return compact.Trim('，', ',', '。', '.', '、', '「', '」', '(', ')', '（', '）');
    }
}
