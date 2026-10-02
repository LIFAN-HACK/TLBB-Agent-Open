using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class MaterialCraftEntryLocator : IMaterialCraftEntryLocator
{
    private const double MinimumScore = 0.92;
    private const double MinimumUniquenessMargin = 0.03;
    private readonly TemplateData? _template;

    public MaterialCraftEntryLocator(string? templatePath = null)
    {
        var path = string.IsNullOrWhiteSpace(templatePath)
            ? Path.Combine(
                AppContext.BaseDirectory,
                "models",
                "vision",
                "v2",
                "material_entry",
                "material_skill_idle.bmp")
            : Path.GetFullPath(templatePath);
        _template = TryLoadBmp(path);
    }

    public bool IsAvailable => _template is not null;
    public string EngineId => "material-entry-compat-ncc-v1";

    public MaterialCraftEntryDetection Locate(CaptureResult frame)
    {
        var template = _template;
        if (template is null)
            return MaterialCraftEntryDetection.Unavailable("材料加工入口模板不可用。");
        if (frame.Width < template.Width || frame.Height < template.Height
            || frame.Pixels.Length < frame.Width * frame.Height * 4)
            return MaterialCraftEntryDetection.Unavailable("客户区截图尺寸无效。");

        var searchX1 = Math.Max(0, frame.Width - 100);
        var searchX2 = frame.Width - template.Width;
        var searchY1 = Math.Max(0, (int)Math.Round(frame.Height * 0.16));
        var searchY2 = Math.Min(frame.Height - template.Height, (int)Math.Round(frame.Height * 0.92));
        if (searchX2 < searchX1 || searchY2 < searchY1)
            return MaterialCraftEntryDetection.Unavailable("右侧技能栏搜索区域无效。");

        var roiWidth = frame.Width - searchX1;
        var roiHeight = searchY2 - searchY1 + template.Height;
        var gray = BuildGrayRoi(frame, searchX1, searchY1, roiWidth, roiHeight);
        var candidates = new List<Candidate>();
        for (var y = 0; y <= roiHeight - template.Height; y++)
        {
            for (var x = 0; x <= roiWidth - template.Width; x++)
            {
                if (!PassesCoarseGate(gray, roiWidth, x, y, template))
                    continue;

                var score = CalculateNcc(gray, roiWidth, x, y, template);
                if (score >= 0.70)
                    candidates.Add(new Candidate(searchX1 + x, searchY1 + y, score));
            }
        }

        if (candidates.Count == 0)
            return new MaterialCraftEntryDetection(false, VisionBox.Empty, 0, EngineId, "右侧技能栏未找到材料加工入口。");

        candidates.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        var best = candidates[0];
        var second = candidates.FirstOrDefault(item =>
            Math.Abs(item.X - best.X) >= template.Width / 2
            || Math.Abs(item.Y - best.Y) >= template.Height / 2);
        var secondScore = second?.Score ?? 0;
        var margin = best.Score - secondScore;
        if (best.Score < MinimumScore || margin < MinimumUniquenessMargin)
        {
            return new MaterialCraftEntryDetection(
                false,
                new VisionBox(best.X, best.Y, template.Width, template.Height),
                best.Score,
                EngineId,
                $"入口候选不唯一：best={best.Score:F3}, second={secondScore:F3}, margin={margin:F3}。");
        }

        return new MaterialCraftEntryDetection(
            true,
            new VisionBox(best.X, best.Y, template.Width, template.Height),
            best.Score,
            EngineId,
            $"入口已确认：score={best.Score:F3}, margin={margin:F3}。");
    }

    private static double[] BuildGrayRoi(
        CaptureResult frame,
        int startX,
        int startY,
        int width,
        int height)
    {
        var output = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = ((startY + y) * frame.Width + startX) * 4;
            var targetOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = sourceOffset + x * 4;
                output[targetOffset + x] = frame.Pixels[pixel] * 0.114
                                           + frame.Pixels[pixel + 1] * 0.587
                                           + frame.Pixels[pixel + 2] * 0.299;
            }
        }

        return output;
    }

    private static bool PassesCoarseGate(
        double[] image,
        int imageWidth,
        int x,
        int y,
        TemplateData template)
    {
        var difference = 0d;
        foreach (var anchor in template.Anchors)
        {
            var imageValue = image[(y + anchor.Y) * imageWidth + x + anchor.X];
            difference += Math.Abs(imageValue - anchor.Value);
        }

        return difference / template.Anchors.Length <= 42;
    }

    private static double CalculateNcc(
        double[] image,
        int imageWidth,
        int x,
        int y,
        TemplateData template)
    {
        var sum = 0d;
        var sumSquares = 0d;
        var cross = 0d;
        var index = 0;
        for (var row = 0; row < template.Height; row++)
        {
            var offset = (y + row) * imageWidth + x;
            for (var column = 0; column < template.Width; column++, index++)
            {
                var value = image[offset + column];
                sum += value;
                sumSquares += value * value;
                cross += value * template.Centered[index];
            }
        }

        var count = template.Width * template.Height;
        var imageEnergy = sumSquares - sum * sum / count;
        if (imageEnergy <= 1e-6)
            return 0;

        return cross / Math.Sqrt(imageEnergy * template.Energy);
    }

    private static TemplateData? TryLoadBmp(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 54 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
                return null;

            var pixelOffset = BitConverter.ToInt32(bytes, 10);
            var width = BitConverter.ToInt32(bytes, 18);
            var signedHeight = BitConverter.ToInt32(bytes, 22);
            var bitsPerPixel = BitConverter.ToInt16(bytes, 28);
            var compression = BitConverter.ToInt32(bytes, 30);
            var height = Math.Abs(signedHeight);
            if (width <= 0 || height <= 0 || bitsPerPixel != 24 || compression != 0)
                return null;

            var stride = (width * 3 + 3) & ~3;
            if (pixelOffset < 0 || pixelOffset + stride * height > bytes.Length)
                return null;

            var gray = new double[width * height];
            for (var y = 0; y < height; y++)
            {
                var sourceY = signedHeight > 0 ? height - 1 - y : y;
                var rowOffset = pixelOffset + sourceY * stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = rowOffset + x * 3;
                    gray[y * width + x] = bytes[pixel] * 0.114
                                                + bytes[pixel + 1] * 0.587
                                                + bytes[pixel + 2] * 0.299;
                }
            }

            var mean = gray.Average();
            var centered = gray.Select(value => value - mean).ToArray();
            var energy = centered.Sum(value => value * value);
            if (energy <= 1e-6)
                return null;

            var anchors = new[]
            {
                new Anchor(3, 3, gray[3 * width + 3]),
                new Anchor(width / 2, 3, gray[3 * width + width / 2]),
                new Anchor(width - 4, 3, gray[3 * width + width - 4]),
                new Anchor(3, height / 2, gray[(height / 2) * width + 3]),
                new Anchor(width / 2, height / 2, gray[(height / 2) * width + width / 2]),
                new Anchor(width - 4, height / 2, gray[(height / 2) * width + width - 4]),
                new Anchor(3, height - 4, gray[(height - 4) * width + 3]),
                new Anchor(width / 2, height - 4, gray[(height - 4) * width + width / 2]),
                new Anchor(width - 4, height - 4, gray[(height - 4) * width + width - 4])
            };
            return new TemplateData(width, height, centered, energy, anchors);
        }
        catch
        {
            return null;
        }
    }

    private sealed record TemplateData(
        int Width,
        int Height,
        double[] Centered,
        double Energy,
        Anchor[] Anchors);

    private sealed record Anchor(int X, int Y, double Value);
    private sealed record Candidate(int X, int Y, double Score);
}
