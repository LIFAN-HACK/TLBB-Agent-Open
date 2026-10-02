using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TlbbAgent.Contracts;
using TlbbAgent.Core.Captcha;

namespace TlbbAgent.Core.Perception;

public sealed class InteractionControlCatalogService : IInteractionControlCatalogService
{
    private const string RootOverrideVariable = "TLBB_AGENT_INTERACTION_CONTROLS_ROOT";
    private readonly Dictionary<string, PanelDefinitionDto> _definitions = new(StringComparer.Ordinal);

    public InteractionControlCatalogService(ILogService log)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable(RootOverrideVariable);
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "models", "vision", "interaction_controls_v3")
                : configured.Trim();
            Load(root);
            log.Info(
                "Vision",
                $"Interaction controls loaded: version={AssetVersion}, classes={_definitions.Count}, authorized={RuntimeAuthorized}.");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            log.Error("Vision", $"Interaction controls unavailable: {ex.Message}");
        }
    }

    public bool IsAvailable { get; private set; }
    public bool RuntimeAuthorized { get; private set; }
    public string AssetVersion { get; private set; } = "";
    public string Message { get; private set; } = "Not loaded.";

    public InteractionPanelControlLayout Map(long frameSequence, InteractionWindowDetection panel)
    {
        if (!IsAvailable || panel.Box.IsEmpty || !_definitions.TryGetValue(panel.ClassName, out var definition))
        {
            return new InteractionPanelControlLayout
            {
                FrameSequence = frameSequence,
                PanelClass = panel.ClassName,
                PanelBox = panel.Box,
                PanelConfidence = panel.Confidence,
                RuntimeAuthorized = false,
                AssetVersion = AssetVersion,
                Message = IsAvailable ? "No canonical controls for this panel class." : Message
            };
        }

        var controls = definition.Controls
            .Select(control => MapControl(panel.Box, definition, control))
            .ToArray();
        var regions = definition.Regions
            .Select(region => MapRegion(panel.Box, definition, region))
            .ToArray();
        return new InteractionPanelControlLayout
        {
            FrameSequence = frameSequence,
            PanelClass = panel.ClassName,
            PanelBox = panel.Box,
            PanelConfidence = panel.Confidence,
            Controls = controls,
            Regions = regions,
            RuntimeAuthorized = RuntimeAuthorized,
            AssetVersion = AssetVersion,
            Message = RuntimeAuthorized
                ? "Canonical controls mapped to current L1 panel."
                : "Observation only: pending explicit user review; input is not authorized."
        };
    }

    public bool TryRenderDebugOverlay(
        CaptureResult frame,
        IReadOnlyList<InteractionPanelControlLayout> layouts,
        string outputPath,
        out string error)
    {
        error = "";
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Pixels.Length < frame.Width * frame.Height * 4)
        {
            error = "Invalid frame.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "Output path is empty.";
            return false;
        }

        try
        {
            var pixels = frame.Pixels.ToArray();
            foreach (var layout in layouts)
            {
                DrawBox(pixels, frame.Width, frame.Height, layout.PanelBox, 40, 40, 240, 2);
                foreach (var region in layout.Regions)
                    DrawBox(pixels, frame.Width, frame.Height, region.Box, 220, 60, 220, 1);
                foreach (var control in layout.Controls)
                {
                    DrawBox(pixels, frame.Width, frame.Height, control.VisualBox, 230, 180, 20, 1);
                    DrawBox(pixels, frame.Width, frame.Height, control.SafeClickBox, 30, 220, 80, 1);
                    if (control.ClickX.HasValue && control.ClickY.HasValue)
                        DrawCross(pixels, frame.Width, frame.Height, control.ClickX.Value, control.ClickY.Value, 20, 230, 240);
                }
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(outputPath, PngEncoder.Encode(frame.Width, frame.Height, pixels));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void Load(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var manifestPath = Path.Combine(fullRoot, "artifact_manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Interaction controls manifest is missing.", manifestPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), options)
                       ?? throw new InvalidDataException("Interaction controls manifest is invalid.");
        if (manifest.Schema != 1 || manifest.AnnotationSchema != 2)
            throw new InvalidDataException("Interaction controls manifest schema mismatch.");
        foreach (var file in manifest.Files)
        {
            var path = ResolveContainedPath(fullRoot, file.Path);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Interaction controls file is missing: {file.Path}", path);
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Interaction controls hash mismatch: {file.Path}");
        }

        var validation = JsonSerializer.Deserialize<ValidationDto>(
                             File.ReadAllText(Path.Combine(fullRoot, "validation.json")),
                             options)
                         ?? throw new InvalidDataException("Interaction controls validation is invalid.");
        if (validation.Schema != 2
            || validation.SampleCount != 25
            || validation.ClassCount != 5
            || validation.ValidationErrorCount != 0)
        {
            throw new InvalidDataException("Interaction controls validation gate failed.");
        }

        var canonicalRoot = Path.Combine(fullRoot, "canonical");
        foreach (var file in Directory.EnumerateFiles(canonicalRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            var definition = JsonSerializer.Deserialize<PanelDefinitionDto>(File.ReadAllText(file), options)
                             ?? throw new InvalidDataException($"Invalid canonical controls: {file}");
            ValidateDefinition(definition, file);
            _definitions.Add(definition.ClassId, definition);
        }

        if (_definitions.Count != 5
            || _definitions.Values.Sum(item => item.Controls.Count) != 46
            || _definitions.Values.Sum(item => item.Regions.Count) != 10)
        {
            throw new InvalidDataException("Interaction controls count gate failed; expected 5 classes, 46 controls and 10 regions.");
        }

        AssetVersion = manifest.ArtifactId;
        RuntimeAuthorized = manifest.RuntimeAuthorized && validation.RuntimeAuthorized;
        IsAvailable = true;
        Message = RuntimeAuthorized
            ? "Loaded and runtime-authorized."
            : "Loaded for observation/replay only; explicit user review is still required.";
    }

    private static void ValidateDefinition(PanelDefinitionDto definition, string source)
    {
        if (definition.Schema != 2
            || string.IsNullOrWhiteSpace(definition.ClassId)
            || definition.CropSize.Length != 2
            || definition.CropSize.Any(value => value <= 0))
        {
            throw new InvalidDataException($"Canonical definition header is invalid: {source}");
        }

        foreach (var control in definition.Controls)
        {
            if (string.IsNullOrWhiteSpace(control.ControlId)
                || control.VisualBoxNorm.Length != 4
                || control.SafeClickBoxNorm.Length != 4
                || control.ClickPointNorm.Length != 2
                || !Contains(control.VisualBoxNorm, control.SafeClickBoxNorm)
                || !ContainsPoint(control.SafeClickBoxNorm, control.ClickPointNorm))
            {
                throw new InvalidDataException($"Canonical control geometry is invalid: {source}:{control.ControlId}");
            }
        }
    }

    private static InteractionControlCandidate MapControl(
        VisionBox panel,
        PanelDefinitionDto definition,
        ControlDto control)
    {
        var visual = MapBox(panel, control.VisualBoxNorm);
        var safe = MapBox(panel, control.SafeClickBoxNorm);
        var point = MapPoint(panel, control.ClickPointNorm);
        return new InteractionControlCandidate
        {
            ControlId = control.ControlId,
            DisplayName = control.DisplayName,
            Kind = control.Kind,
            Gesture = control.Gesture,
            VisualBox = visual,
            SafeClickBox = safe,
            ClickX = point.X,
            ClickY = point.Y,
            Risk = control.Risk,
            RuntimeDefault = control.RuntimeDefault,
            Preconditions = control.Preconditions,
            ExpectedPostconditions = control.ExpectedPostconditions,
            FailurePolicy = control.FailurePolicy
        };
    }

    private static InteractionRegionCandidate MapRegion(
        VisionBox panel,
        PanelDefinitionDto definition,
        RegionDto region)
    {
        var cells = region.Kind switch
        {
            "regular_grid" => MapRegularGrid(panel, definition, region),
            "explicit_grid" => MapExplicitGrid(panel, definition, region),
            _ => Array.Empty<InteractionGridCellCandidate>()
        };
        var box = region.BoxNorm.Length == 4
            ? MapBox(panel, region.BoxNorm)
            : Union(cells.Select(cell => cell.CellBox));
        return new InteractionRegionCandidate
        {
            RegionId = region.RegionId,
            DisplayName = region.DisplayName,
            Kind = region.Kind,
            Box = box,
            Rows = region.Rows > 0 ? region.Rows : region.YStartsPx.Length,
            Columns = region.Columns > 0 ? region.Columns : region.XStartsPx.Length,
            Cells = cells,
            FailurePolicy = region.FailurePolicy
        };
    }

    private static InteractionGridCellCandidate[] MapRegularGrid(
        VisionBox panel,
        PanelDefinitionDto definition,
        RegionDto region)
    {
        if (region.Rows <= 0 || region.Columns <= 0 || region.BoxPx.Length != 4)
            return Array.Empty<InteractionGridCellCandidate>();
        var width = region.CellSizePx.Length == 2
            ? region.CellSizePx[0]
            : region.CellPitchPx.Length == 2
                ? region.BoxPx[2] - region.BoxPx[0] - region.CellPitchPx[0] * (region.Columns - 1)
                : (region.BoxPx[2] - region.BoxPx[0]) / region.Columns;
        var height = region.CellSizePx.Length == 2
            ? region.CellSizePx[1]
            : region.CellPitchPx.Length == 2
                ? region.BoxPx[3] - region.BoxPx[1] - region.CellPitchPx[1] * (region.Rows - 1)
                : (region.BoxPx[3] - region.BoxPx[1]) / region.Rows;
        var pitchX = region.CellPitchPx.Length == 2 ? region.CellPitchPx[0] : width;
        var pitchY = region.CellPitchPx.Length == 2 ? region.CellPitchPx[1] : height;
        var output = new List<InteractionGridCellCandidate>(region.Rows * region.Columns);
        for (var row = 0; row < region.Rows; row++)
        {
            for (var column = 0; column < region.Columns; column++)
            {
                output.Add(MapCell(
                    panel,
                    definition,
                    region,
                    row,
                    column,
                    region.BoxPx[0] + column * pitchX,
                    region.BoxPx[1] + row * pitchY,
                    width,
                    height));
            }
        }
        return output.ToArray();
    }

    private static InteractionGridCellCandidate[] MapExplicitGrid(
        VisionBox panel,
        PanelDefinitionDto definition,
        RegionDto region)
    {
        if (region.XStartsPx.Length == 0 || region.YStartsPx.Length == 0 || region.CellSizePx.Length != 2)
            return Array.Empty<InteractionGridCellCandidate>();
        var output = new List<InteractionGridCellCandidate>(region.XStartsPx.Length * region.YStartsPx.Length);
        for (var row = 0; row < region.YStartsPx.Length; row++)
        {
            for (var column = 0; column < region.XStartsPx.Length; column++)
            {
                output.Add(MapCell(
                    panel,
                    definition,
                    region,
                    row,
                    column,
                    region.XStartsPx[column],
                    region.YStartsPx[row],
                    region.CellSizePx[0],
                    region.CellSizePx[1]));
            }
        }
        return output.ToArray();
    }

    private static InteractionGridCellCandidate MapCell(
        VisionBox panel,
        PanelDefinitionDto definition,
        RegionDto region,
        int row,
        int column,
        int x,
        int y,
        int width,
        int height)
    {
        var box = MapCanonicalBox(panel, definition, x, y, x + width, y + height);
        var inset = region.SafeCellInsetPx.Length == 4 ? region.SafeCellInsetPx : new[] { 0, 0, 0, 0 };
        var safe = MapCanonicalBox(
            panel,
            definition,
            x + inset[0],
            y + inset[1],
            x + width - inset[2],
            y + height - inset[3]);
        var disabled = region.DisabledCells.Any(value => value.Length == 2 && value[0] == row && value[1] == column);
        return new InteractionGridCellCandidate
        {
            RegionId = region.RegionId,
            Row = row,
            Column = column,
            CellBox = box,
            SafeClickBox = safe,
            ClickX = disabled ? null : safe.X + safe.Width / 2,
            ClickY = disabled ? null : safe.Y + safe.Height / 2,
            IsDisabled = disabled
        };
    }

    private static VisionBox MapCanonicalBox(
        VisionBox panel,
        PanelDefinitionDto definition,
        int x1,
        int y1,
        int x2,
        int y2) =>
        MapBox(panel, new[]
        {
            x1 / (double)definition.CropSize[0],
            y1 / (double)definition.CropSize[1],
            x2 / (double)definition.CropSize[0],
            y2 / (double)definition.CropSize[1]
        });

    private static VisionBox MapBox(VisionBox panel, IReadOnlyList<double> normalized)
    {
        if (normalized.Count != 4 || panel.IsEmpty)
            return VisionBox.Empty;
        var left = panel.X + (int)Math.Round(normalized[0] * panel.Width);
        var top = panel.Y + (int)Math.Round(normalized[1] * panel.Height);
        var right = panel.X + (int)Math.Round(normalized[2] * panel.Width);
        var bottom = panel.Y + (int)Math.Round(normalized[3] * panel.Height);
        left = Math.Clamp(left, panel.X, panel.Right);
        top = Math.Clamp(top, panel.Y, panel.Bottom);
        right = Math.Clamp(right, left, panel.Right);
        bottom = Math.Clamp(bottom, top, panel.Bottom);
        return VisionBox.FromXyxy(left, top, right, bottom);
    }

    private static (int X, int Y) MapPoint(VisionBox panel, IReadOnlyList<double> normalized) =>
        normalized.Count == 2
            ? (
                Math.Clamp(panel.X + (int)Math.Round(normalized[0] * panel.Width), panel.X, panel.Right - 1),
                Math.Clamp(panel.Y + (int)Math.Round(normalized[1] * panel.Height), panel.Y, panel.Bottom - 1))
            : (panel.X, panel.Y);

    private static VisionBox Union(IEnumerable<VisionBox> boxes)
    {
        var values = boxes.Where(box => !box.IsEmpty).ToArray();
        return values.Length == 0
            ? VisionBox.Empty
            : VisionBox.FromXyxy(
                values.Min(box => box.X),
                values.Min(box => box.Y),
                values.Max(box => box.Right),
                values.Max(box => box.Bottom));
    }

    private static bool Contains(IReadOnlyList<double> outer, IReadOnlyList<double> inner) =>
        outer.Count == 4
        && inner.Count == 4
        && inner[0] >= outer[0]
        && inner[1] >= outer[1]
        && inner[2] <= outer[2]
        && inner[3] <= outer[3]
        && inner[2] > inner[0]
        && inner[3] > inner[1];

    private static bool ContainsPoint(IReadOnlyList<double> outer, IReadOnlyList<double> point) =>
        outer.Count == 4
        && point.Count == 2
        && point[0] >= outer[0]
        && point[0] < outer[2]
        && point[1] >= outer[1]
        && point[1] < outer[3];

    private static string ResolveContainedPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Interaction controls path escapes root: {relative}");
        return path;
    }

    private static void DrawBox(
        byte[] pixels,
        int width,
        int height,
        VisionBox box,
        byte red,
        byte green,
        byte blue,
        int thickness)
    {
        if (box.IsEmpty)
            return;
        for (var offset = 0; offset < thickness; offset++)
        {
            var left = Math.Clamp(box.X + offset, 0, width - 1);
            var right = Math.Clamp(box.Right - 1 - offset, 0, width - 1);
            var top = Math.Clamp(box.Y + offset, 0, height - 1);
            var bottom = Math.Clamp(box.Bottom - 1 - offset, 0, height - 1);
            for (var x = left; x <= right; x++)
            {
                SetPixel(pixels, width, x, top, red, green, blue);
                SetPixel(pixels, width, x, bottom, red, green, blue);
            }
            for (var y = top; y <= bottom; y++)
            {
                SetPixel(pixels, width, left, y, red, green, blue);
                SetPixel(pixels, width, right, y, red, green, blue);
            }
        }
    }

    private static void DrawCross(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        byte red,
        byte green,
        byte blue)
    {
        for (var offset = -3; offset <= 3; offset++)
        {
            if (x + offset >= 0 && x + offset < width && y >= 0 && y < height)
                SetPixel(pixels, width, x + offset, y, red, green, blue);
            if (x >= 0 && x < width && y + offset >= 0 && y + offset < height)
                SetPixel(pixels, width, x, y + offset, red, green, blue);
        }
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y, byte red, byte green, byte blue)
    {
        var index = (y * width + x) * 4;
        pixels[index] = blue;
        pixels[index + 1] = green;
        pixels[index + 2] = red;
        pixels[index + 3] = 255;
    }

    private sealed class ManifestDto
    {
        [JsonPropertyName("schema")] public int Schema { get; init; }
        [JsonPropertyName("artifact_id")] public string ArtifactId { get; init; } = "";
        [JsonPropertyName("annotation_schema")] public int AnnotationSchema { get; init; }
        [JsonPropertyName("runtime_authorized")] public bool RuntimeAuthorized { get; init; }
        [JsonPropertyName("files")] public List<ManifestFileDto> Files { get; init; } = new();
    }

    private sealed class ManifestFileDto
    {
        [JsonPropertyName("path")] public string Path { get; init; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    }

    private sealed class ValidationDto
    {
        [JsonPropertyName("schema")] public int Schema { get; init; }
        [JsonPropertyName("runtime_authorized")] public bool RuntimeAuthorized { get; init; }
        [JsonPropertyName("sample_count")] public int SampleCount { get; init; }
        [JsonPropertyName("class_count")] public int ClassCount { get; init; }
        [JsonPropertyName("validation_error_count")] public int ValidationErrorCount { get; init; }
    }

    private sealed class PanelDefinitionDto
    {
        [JsonPropertyName("schema")] public int Schema { get; init; }
        [JsonPropertyName("class_id")] public string ClassId { get; init; } = "";
        [JsonPropertyName("crop_size")] public int[] CropSize { get; init; } = Array.Empty<int>();
        [JsonPropertyName("controls")] public List<ControlDto> Controls { get; init; } = new();
        [JsonPropertyName("regions")] public List<RegionDto> Regions { get; init; } = new();
    }

    private sealed class ControlDto
    {
        [JsonPropertyName("control_id")] public string ControlId { get; init; } = "";
        [JsonPropertyName("zh_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";
        [JsonPropertyName("gesture")] public string Gesture { get; init; } = "";
        [JsonPropertyName("visual_bbox_norm")] public double[] VisualBoxNorm { get; init; } = Array.Empty<double>();
        [JsonPropertyName("safe_click_bbox_norm")] public double[] SafeClickBoxNorm { get; init; } = Array.Empty<double>();
        [JsonPropertyName("click_point_norm")] public double[] ClickPointNorm { get; init; } = Array.Empty<double>();
        [JsonPropertyName("risk")] public string Risk { get; init; } = "";
        [JsonPropertyName("runtime_default")] public string RuntimeDefault { get; init; } = "";
        [JsonPropertyName("preconditions")] public string[] Preconditions { get; init; } = Array.Empty<string>();
        [JsonPropertyName("expected_postconditions")] public string[] ExpectedPostconditions { get; init; } = Array.Empty<string>();
        [JsonPropertyName("failure_policy")] public string FailurePolicy { get; init; } = "";
    }

    private sealed class RegionDto
    {
        [JsonPropertyName("region_id")] public string RegionId { get; init; } = "";
        [JsonPropertyName("zh_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";
        [JsonPropertyName("rows")] public int Rows { get; init; }
        [JsonPropertyName("columns")] public int Columns { get; init; }
        [JsonPropertyName("bbox_xyxy")] public int[] BoxPx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("bbox_norm")] public double[] BoxNorm { get; init; } = Array.Empty<double>();
        [JsonPropertyName("cell_size_px")] public int[] CellSizePx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("cell_pitch_px")] public int[] CellPitchPx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("x_starts_px")] public int[] XStartsPx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("y_starts_px")] public int[] YStartsPx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("safe_cell_inset_px")] public int[] SafeCellInsetPx { get; init; } = Array.Empty<int>();
        [JsonPropertyName("disabled_cells_zero_based")] public int[][] DisabledCells { get; init; } = Array.Empty<int[]>();
        [JsonPropertyName("failure_policy")] public string FailurePolicy { get; init; } = "";
    }
}
