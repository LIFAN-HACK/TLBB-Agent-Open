using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class InventoryRecognitionService : IInventoryRecognitionService
{
    private const string Module = "Inventory";
    private static readonly GridShape[] CommonShapes =
    [
        new(10, 6, 32, 32, 3, 3),
        new(10, 5, 32, 32, 3, 3),
        new(10, 6, 34, 34, 1, 1),
        new(10, 5, 34, 34, 1, 1),
        new(8, 5, 40, 40, 3, 3),
        new(8, 6, 40, 40, 3, 3),
        new(8, 5, 38, 38, 2, 2),
        new(8, 6, 38, 38, 2, 2)
    ];

    private readonly IWindowCaptureService _capture;
    private readonly ILogService _log;
    private readonly IInteractionWindowDetectService? _interactionWindows;
    private readonly IInteractionControlCatalogService? _interactionControls;

    public InventoryRecognitionService(IWindowCaptureService capture, ILogService log)
        : this(capture, log, null, null)
    {
    }

    public InventoryRecognitionService(
        IWindowCaptureService capture,
        ILogService log,
        IInteractionWindowDetectService? interactionWindows,
        IInteractionControlCatalogService? interactionControls)
    {
        _capture = capture;
        _log = log;
        _interactionWindows = interactionWindows;
        _interactionControls = interactionControls;
    }

    public InventoryGridCalibrationResult AutoCalibrate(BoundGameWindow window)
    {
        var capture = _capture.CaptureClient(window.Hwnd);
        if (capture is null)
            return new InventoryGridCalibrationResult(false, "客户区截图失败，无法校准背包。", null, null);

        var result = AutoCalibrate(capture);
        if (result.Success)
            _log.Info(Module, result.Message, window.Id);
        else
            _log.Warn(Module, result.Message, window.Id);

        return result;
    }

    public InventoryGridCalibrationResult AutoCalibrate(CaptureResult capture)
    {
        if (_interactionWindows is not null || _interactionControls is not null)
            return AutoCalibrateFromInteractionModel(capture);

        var best = FindBestGrid(capture);
        if (best is null || best.Value.Score < 0.42)
        {
            return new InventoryGridCalibrationResult(
                false,
                "未检测到可靠背包网格；请先打开背包，或手动填写第一格坐标/行列/格子大小。",
                null,
                null);
        }

        var grid = new InventoryGridConfig
        {
            ScanMode = InventoryScanMode.ManualGrid,
            Enabled = true,
            X = best.Value.X,
            Y = best.Value.Y,
            Columns = best.Value.Shape.Columns,
            Rows = best.Value.Shape.Rows,
            CellWidth = best.Value.Shape.CellWidth,
            CellHeight = best.Value.Shape.CellHeight,
            GapX = best.Value.Shape.GapX,
            GapY = best.Value.Shape.GapY
        };
        var scan = Scan(capture, grid);
        return new InventoryGridCalibrationResult(
            true,
            $"背包网格校准完成：({grid.X},{grid.Y}) {grid.Columns}x{grid.Rows}，格 {grid.CellWidth}x{grid.CellHeight}，间距 {grid.GapX}/{grid.GapY}，置信度 {best.Value.Score:0.00}。",
            grid,
            scan);
    }

    private InventoryGridCalibrationResult AutoCalibrateFromInteractionModel(CaptureResult capture)
    {
        if (_interactionWindows is null || _interactionControls is null || !_interactionControls.IsAvailable)
        {
            return new InventoryGridCalibrationResult(
                false,
                "背包交互控件资产不可用，已拒绝旧全屏颜色扫描回退。",
                null,
                null);
        }

        var panel = _interactionWindows
            .Detect(capture)
            .Detections
            .Where(item => string.Equals(item.ClassName, "bag_panel", StringComparison.Ordinal))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (panel is null || panel.Confidence < 0.75)
        {
            return new InventoryGridCalibrationResult(
                false,
                "当前帧未可靠识别到背包面板，未生成物品格网格。",
                null,
                null);
        }

        var layout = _interactionControls.Map(capture.Sequence, panel);
        var region = layout.Regions.SingleOrDefault(item =>
            string.Equals(item.RegionId, "bag_cells", StringComparison.Ordinal)
            && string.Equals(item.Kind, "regular_grid", StringComparison.OrdinalIgnoreCase));
        if (layout.FrameSequence != capture.Sequence
            || region is null
            || region.Rows != 6
            || region.Columns != 10
            || region.Cells.Count != 60)
        {
            return new InventoryGridCalibrationResult(
                false,
                "当前帧未映射出完整 10x6 背包物品格，已安全停止。",
                null,
                null);
        }

        var first = region.Cells.SingleOrDefault(item => item.Row == 0 && item.Column == 0);
        var secondColumn = region.Cells.SingleOrDefault(item => item.Row == 0 && item.Column == 1);
        var secondRow = region.Cells.SingleOrDefault(item => item.Row == 1 && item.Column == 0);
        if (first is null
            || secondColumn is null
            || secondRow is null
            || first.CellBox.IsEmpty
            || secondColumn.CellBox.IsEmpty
            || secondRow.CellBox.IsEmpty)
        {
            return new InventoryGridCalibrationResult(
                false,
                "背包物品格几何不完整，已安全停止。",
                null,
                null);
        }

        var gapX = secondColumn.CellBox.X - first.CellBox.X - first.CellBox.Width;
        var gapY = secondRow.CellBox.Y - first.CellBox.Y - first.CellBox.Height;
        if (gapX < 0 || gapY < 0)
        {
            return new InventoryGridCalibrationResult(
                false,
                "背包物品格间距无效，已安全停止。",
                null,
                null);
        }

        var grid = new InventoryGridConfig
        {
            ScanMode = InventoryScanMode.ManualGrid,
            Enabled = true,
            X = first.CellBox.X,
            Y = first.CellBox.Y,
            Columns = region.Columns,
            Rows = region.Rows,
            CellWidth = first.CellBox.Width,
            CellHeight = first.CellBox.Height,
            GapX = gapX,
            GapY = gapY
        };
        var scan = Scan(capture, grid);
        if (!scan.Success)
            return new InventoryGridCalibrationResult(false, scan.Message, null, scan);

        return new InventoryGridCalibrationResult(
            true,
            $"背包网格由当前帧 V3 面板映射完成：panel={panel.Confidence:P0}，grid=({grid.X},{grid.Y}) 10x6，frame={capture.Sequence}。",
            grid,
            scan);
    }

    public InventoryGridProbeResult ProbeGridCandidates(BoundGameWindow window, int maxCandidates)
    {
        var capture = _capture.CaptureClient(window.Hwnd);
        if (capture is null)
            return new InventoryGridProbeResult(false, "客户区截图失败，无法探测背包候选网格。", Array.Empty<InventoryGridCandidate>());

        var result = ProbeGridCandidates(capture, maxCandidates);
        if (result.Success)
            _log.Info(Module, result.Message, window.Id);
        else
            _log.Warn(Module, result.Message, window.Id);

        return result;
    }

    public InventoryGridProbeResult ProbeGridCandidates(CaptureResult capture, int maxCandidates)
    {
        if (_interactionWindows is not null || _interactionControls is not null)
        {
            var calibration = AutoCalibrateFromInteractionModel(capture);
            if (!calibration.Success || calibration.Grid is null)
            {
                return new InventoryGridProbeResult(
                    false,
                    calibration.Message,
                    Array.Empty<InventoryGridCandidate>());
            }

            return new InventoryGridProbeResult(
                true,
                $"V3 背包面板仅生成一个受控 10x6 网格候选：{calibration.Message}",
                [new InventoryGridCandidate(
                    calibration.Grid,
                    1.0,
                    true,
                    "interaction-window-v3/bag_cells")]);
        }

        var candidates = FindGridCandidates(capture, Math.Clamp(maxCandidates, 1, 12))
            .Select(c => new InventoryGridCandidate(
                ToGridConfig(c),
                c.Score,
                IsLikelyInventoryCandidate(capture, c),
                c.Source))
            .ToArray();

        if (candidates.Length == 0)
            return new InventoryGridProbeResult(false, "未找到背包候选网格；请确认背包已打开，或改用手动 grid。", candidates);

        var reliable = candidates.Count(c => c.IsLikelyInventory && c.Score >= 0.42);
        var message = reliable > 0
            ? $"找到 {candidates.Length} 个背包候选网格，其中 {reliable} 个达到自动校准门限。"
            : $"找到 {candidates.Length} 个候选网格，但未达到自动校准门限；请查看 overlay 后手动确认。";

        return new InventoryGridProbeResult(true, message, candidates);
    }

    public InventoryScanResult Scan(BoundGameWindow window, InventoryGridConfig config)
    {
        var capture = _capture.CaptureClient(window.Hwnd);
        if (capture is null)
            return Empty(false, "客户区截图失败，无法扫描背包。");

        var result = Scan(capture, config);
        if (!result.Success)
            _log.Warn(Module, result.Message, window.Id);
        else
            _log.Info(Module, $"背包扫描完成：占用 {result.OccupiedCount}，空格 {result.EmptyCount}。", window.Id);

        return result;
    }

    public InventoryScanResult Scan(CaptureResult capture, InventoryGridConfig config)
    {
        Normalize(config);
        if (config.ScanMode == InventoryScanMode.AutoDetect)
        {
            var calibration = AutoCalibrate(capture);
            if (!calibration.Success || calibration.Grid is null)
                return Empty(false, calibration.Message, capture);

            config = CloneGrid(calibration.Grid);
        }

        if (!config.Enabled)
            return Empty(false, "未配置背包格子区域；请先用采集/校准工具框定背包第一格。", capture);

        var totalWidth = config.Columns * config.CellWidth + Math.Max(0, config.Columns - 1) * config.GapX;
        var totalHeight = config.Rows * config.CellHeight + Math.Max(0, config.Rows - 1) * config.GapY;
        if (config.X < 0 || config.Y < 0 || config.X + totalWidth > capture.Width || config.Y + totalHeight > capture.Height)
            return Empty(false, $"背包格子区域超出客户区：grid=({config.X},{config.Y},{totalWidth},{totalHeight}) client={capture.Width}x{capture.Height}。", capture);

        var slots = new List<InventorySlotSnapshot>(config.Columns * config.Rows);
        for (var row = 0; row < config.Rows; row++)
        {
            for (var col = 0; col < config.Columns; col++)
            {
                var index = row * config.Columns + col;
                var x = config.X + col * (config.CellWidth + config.GapX);
                var y = config.Y + row * (config.CellHeight + config.GapY);
                var score = ScoreSlot(capture, x, y, config.CellWidth, config.CellHeight);
                var state = score >= 0.18 ? InventorySlotState.Occupied : InventorySlotState.Empty;
                config.SlotNameOverrides.TryGetValue(index, out var name);
                config.SlotNameSources.TryGetValue(index, out var nameSource);
                slots.Add(new InventorySlotSnapshot(
                    index,
                    row,
                    col,
                    x,
                    y,
                    config.CellWidth,
                    config.CellHeight,
                    state,
                    name?.Trim() ?? "",
                    score,
                    string.IsNullOrWhiteSpace(name) ? "grid-color" : string.IsNullOrWhiteSpace(nameSource) ? "manual-label" : nameSource.Trim()));
            }
        }

        return new InventoryScanResult(true, "背包格子扫描完成。", capture.Width, capture.Height, slots);
    }

    private static InventoryScanResult Empty(bool success, string message, CaptureResult? capture = null) =>
        new(success, message, capture?.Width ?? 0, capture?.Height ?? 0, Array.Empty<InventorySlotSnapshot>());

    private static void Normalize(InventoryGridConfig config)
    {
        config.Columns = Math.Clamp(config.Columns, 1, 12);
        config.Rows = Math.Clamp(config.Rows, 1, 12);
        config.CellWidth = Math.Clamp(config.CellWidth, 12, 96);
        config.CellHeight = Math.Clamp(config.CellHeight, 12, 96);
        config.GapX = Math.Clamp(config.GapX, 0, 16);
        config.GapY = Math.Clamp(config.GapY, 0, 16);
        config.SlotNameOverrides ??= new Dictionary<int, string>();
        config.SlotNameSources ??= new Dictionary<int, string>();
    }

    private static InventoryGridConfig CloneGrid(InventoryGridConfig config) =>
        new()
        {
            ScanMode = config.ScanMode,
            Enabled = config.Enabled,
            X = config.X,
            Y = config.Y,
            Columns = config.Columns,
            Rows = config.Rows,
            CellWidth = config.CellWidth,
            CellHeight = config.CellHeight,
            GapX = config.GapX,
            GapY = config.GapY,
            SlotNameOverrides = new Dictionary<int, string>(config.SlotNameOverrides),
            SlotNameSources = new Dictionary<int, string>(config.SlotNameSources)
        };

    private static GridCandidate? FindBestGrid(CaptureResult image)
    {
        var lineCandidate = FindByInventoryGridLines(image);
        if (lineCandidate is not null && IsLikelyInventoryCandidate(image, lineCandidate.Value))
            return lineCandidate;

        var shapes = BuildShapes(image).ToArray();
        var best = default(GridCandidate?);
        foreach (var shape in shapes)
        {
            var totalWidth = shape.TotalWidth;
            var totalHeight = shape.TotalHeight;
            if (totalWidth >= image.Width || totalHeight >= image.Height)
                continue;

            var xStart = image.Width < 420 ? 0 : Math.Max(0, image.Width * 50 / 100);
            var yStart = image.Height < 320 ? 0 : Math.Max(0, image.Height * 30 / 100);
            var xEnd = Math.Max(xStart, Math.Min(image.Width - totalWidth - 4, image.Width * 86 / 100));
            var yEnd = Math.Max(yStart, Math.Min(image.Height - totalHeight - 4, image.Height * 78 / 100));
            var step = image.Width < 420 ? 2 : 8;

            for (var y = yStart; y <= yEnd; y += step)
            {
                for (var x = xStart; x <= xEnd; x += step)
                {
                    var score = ScoreGridFrame(image, x, y, shape);
                    if (IsBetterCandidate(score, x, y, best))
                        best = new GridCandidate(x, y, shape, score);
                }
            }
        }

        if (best is null)
            return null;

        var refined = RefineCandidate(image, best.Value);
        return IsLikelyInventoryCandidate(image, refined) ? refined : null;
    }

    private static IReadOnlyList<GridCandidate> FindGridCandidates(CaptureResult image, int maxCandidates)
    {
        var candidates = new List<GridCandidate>();
        var lineCandidate = FindByInventoryGridLines(image);
        if (lineCandidate is not null)
            AddCandidate(candidates, lineCandidate.Value with { Source = "line-runs" }, image, maxCandidates);

        foreach (var shape in BuildShapes(image))
        {
            var totalWidth = shape.TotalWidth;
            var totalHeight = shape.TotalHeight;
            if (totalWidth >= image.Width || totalHeight >= image.Height)
                continue;

            var coarseCandidates = new List<GridCandidate>();

            var xStart = image.Width < 420 ? 0 : Math.Max(0, image.Width * 50 / 100);
            var yStart = image.Height < 320 ? 0 : Math.Max(0, image.Height * 30 / 100);
            var xEnd = Math.Max(xStart, Math.Min(image.Width - totalWidth - 4, image.Width * 86 / 100));
            var yEnd = Math.Max(yStart, Math.Min(image.Height - totalHeight - 4, image.Height * 78 / 100));
            var step = image.Width < 420 ? 2 : 8;

            for (var y = yStart; y <= yEnd; y += step)
            {
                for (var x = xStart; x <= xEnd; x += step)
                {
                    var score = ScoreGridFrame(image, x, y, shape);
                    if (score < 0.18)
                        continue;

                    AddCoarseCandidate(coarseCandidates, new GridCandidate(x, y, shape, score, "scan"), Math.Max(maxCandidates * 2, 6));
                }
            }

            foreach (var coarse in coarseCandidates)
            {
                var refined = RefineCandidate(image, coarse);
                AddCandidate(candidates, refined, image, maxCandidates);
            }
        }

        return candidates
            .OrderByDescending(c => IsLikelyInventoryCandidate(image, c))
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Y)
            .ThenBy(c => c.X)
            .Take(maxCandidates)
            .ToArray();
    }

    private static void AddCandidate(List<GridCandidate> candidates, GridCandidate candidate, CaptureResult image, int maxCandidates)
    {
        if (candidate.X < 0 || candidate.Y < 0 || candidate.X + candidate.Shape.TotalWidth >= image.Width || candidate.Y + candidate.Shape.TotalHeight >= image.Height)
            return;

        var existing = candidates.FindIndex(c => IsSameCandidateArea(c, candidate));
        if (existing >= 0)
        {
            if (candidate.Score > candidates[existing].Score)
                candidates[existing] = candidate;
            return;
        }

        candidates.Add(candidate);
        candidates.Sort((a, b) =>
        {
            var likely = IsLikelyInventoryCandidate(image, b).CompareTo(IsLikelyInventoryCandidate(image, a));
            if (likely != 0)
                return likely;

            var score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;

            var y = a.Y.CompareTo(b.Y);
            return y != 0 ? y : a.X.CompareTo(b.X);
        });

        if (candidates.Count > Math.Max(maxCandidates * 3, maxCandidates))
            candidates.RemoveRange(maxCandidates * 3, candidates.Count - maxCandidates * 3);
    }

    private static void AddCoarseCandidate(List<GridCandidate> candidates, GridCandidate candidate, int limit)
    {
        var existing = candidates.FindIndex(c => IsSameCandidateArea(c, candidate));
        if (existing >= 0)
        {
            if (candidate.Score > candidates[existing].Score)
                candidates[existing] = candidate;
            return;
        }

        candidates.Add(candidate);
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > limit)
            candidates.RemoveRange(limit, candidates.Count - limit);
    }

    private static bool IsSameCandidateArea(GridCandidate left, GridCandidate right)
    {
        return Math.Abs(left.X - right.X) <= 10
               && Math.Abs(left.Y - right.Y) <= 10
               && Math.Abs(left.Shape.TotalWidth - right.Shape.TotalWidth) <= 18
               && Math.Abs(left.Shape.TotalHeight - right.Shape.TotalHeight) <= 18;
    }

    private static InventoryGridConfig ToGridConfig(GridCandidate candidate) =>
        new()
        {
            ScanMode = InventoryScanMode.ManualGrid,
            Enabled = true,
            X = candidate.X,
            Y = candidate.Y,
            Columns = candidate.Shape.Columns,
            Rows = candidate.Shape.Rows,
            CellWidth = candidate.Shape.CellWidth,
            CellHeight = candidate.Shape.CellHeight,
            GapX = candidate.Shape.GapX,
            GapY = candidate.Shape.GapY
        };

    private static GridCandidate? FindByInventoryGridLines(CaptureResult image)
    {
        if (image.Width < 420 || image.Height < 320)
            return null;

        var runs = new List<HorizontalRun>();
        var xMin = Math.Max(0, image.Width * 50 / 100);
        var xMax = Math.Min(image.Width - 1, image.Width * 86 / 100);
        var yMin = Math.Max(0, image.Height * 30 / 100);
        var yMax = Math.Min(image.Height - 1, image.Height * 78 / 100);

        for (var y = yMin; y <= yMax; y++)
        {
            var inRun = false;
            var start = 0;
            for (var x = xMin; x <= xMax; x++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                var gold = LooksLikeInventoryGridLinePixel(r, g, b);
                if (gold && !inRun)
                {
                    start = x;
                    inRun = true;
                }

                if ((!gold || x == xMax) && inRun)
                {
                    var end = gold && x == xMax ? x : x - 1;
                    var width = end - start + 1;
                    if (width is >= 300 and <= 390)
                        runs.Add(new HorizontalRun(y, start, end));
                    inRun = false;
                }
            }
        }

        if (runs.Count < 5)
            return null;

        foreach (var anchor in runs.OrderBy(r => r.Y))
        {
            var group = runs
                .Where(r => Math.Abs(r.X1 - anchor.X1) <= 8 && Math.Abs(r.Width - anchor.Width) <= 22 && r.Y >= anchor.Y)
                .OrderBy(r => r.Y)
                .ToList();

            var compact = new List<HorizontalRun>();
            foreach (var run in group)
            {
                if (compact.Count == 0 || run.Y - compact[^1].Y >= 20)
                    compact.Add(run);
            }

            if (compact.Count < 5)
                continue;

            var diffs = compact.Zip(compact.Skip(1), (a, b) => b.Y - a.Y)
                .Where(d => d is >= 28 and <= 45)
                .ToArray();
            if (diffs.Length < 4)
                continue;

            var pitchY = diffs.OrderBy(d => d).ElementAt(diffs.Length / 2);
            var rows = Math.Clamp(compact.Count - 1, 5, 6);
            var x = (int)Math.Round(compact.Take(rows + 1).Average(r => r.X1));
            var y = compact[0].Y;
            var runWidth = (int)Math.Round(compact.Take(rows + 1).Average(r => r.Width));
            var pitchX = Math.Clamp((int)Math.Round(runWidth / 10.0), 32, 38);
            var gapX = Math.Clamp(pitchX <= 33 ? 2 : 3, 1, 4);
            var gapY = Math.Clamp(pitchY <= 33 ? 2 : 3, 1, 4);
            var shape = new GridShape(10, rows, pitchX - gapX, pitchY - gapY, gapX, gapY);
            if (x + shape.TotalWidth >= image.Width || y + shape.TotalHeight >= image.Height)
                continue;

            var score = Math.Max(0.88, ScoreGridFrame(image, x, y, shape, full: true));
            return new GridCandidate(x, y, shape, score);
        }

        return null;
    }

    private static IEnumerable<GridShape> BuildShapes(CaptureResult image)
    {
        if (image.Width < 420 || image.Height < 320)
        {
            yield return new GridShape(3, 2, 32, 32, 4, 4);
            yield return new GridShape(4, 3, 32, 32, 4, 4);
        }

        foreach (var shape in CommonShapes)
            yield return shape;
    }

    private static GridCandidate RefineCandidate(CaptureResult image, GridCandidate candidate)
    {
        var best = candidate;
        for (var y = candidate.Y - 8; y <= candidate.Y + 8; y += 2)
        {
            for (var x = candidate.X - 8; x <= candidate.X + 8; x += 2)
            {
                if (x < 0 || y < 0 || x + candidate.Shape.TotalWidth >= image.Width || y + candidate.Shape.TotalHeight >= image.Height)
                    continue;

                var score = ScoreGridFrame(image, x, y, candidate.Shape, full: true);
                if (IsBetterCandidate(score, x, y, best))
                    best = new GridCandidate(x, y, candidate.Shape, score);
            }
        }

        return best;
    }

    private static bool IsBetterCandidate(double score, int x, int y, GridCandidate? current)
    {
        if (current is null)
            return true;

        if (score > current.Value.Score + 0.012)
            return true;

        if (score < current.Value.Score - 0.012)
            return false;

        if (y < current.Value.Y - 8)
            return true;

        if (Math.Abs(y - current.Value.Y) <= 8 && x < current.Value.X)
            return true;

        return false;
    }

    private static bool IsLikelyInventoryCandidate(CaptureResult image, GridCandidate candidate)
    {
        if (image.Width < 420 || image.Height < 320)
            return true;

        // The current TLBB bag panel uses a 10-column inventory grid. Smaller
        // grids are too easy to confuse with server lists, side panels or chat
        // buttons, so live auto-detection must stay conservative.
        if (candidate.Shape.Columns != 10)
            return false;

        return HasInventoryHeaderRun(image, candidate) && HasInventoryTabRow(image, candidate);
    }

    private static bool HasInventoryHeaderRun(CaptureResult image, GridCandidate candidate)
    {
        var totalWidth = candidate.Shape.TotalWidth;
        var left = Math.Max(0, candidate.X - 30);
        var right = Math.Min(image.Width - 1, candidate.X + totalWidth + 30);
        var top = Math.Max(0, candidate.Y - 86);
        var bottom = Math.Max(0, candidate.Y - 38);
        var required = Math.Min(Math.Max(180, totalWidth * 3 / 5), right - left + 1);

        for (var y = top; y <= bottom; y++)
        {
            var run = 0;
            for (var x = left; x <= right; x++)
            {
                var p = (y * image.Width + x) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                if (LooksLikeInventoryGridLinePixel(r, g, b) || LooksLikeInventoryTitlePixel(r, g, b))
                {
                    run++;
                    if (run >= required)
                        return true;
                }
                else
                {
                    run = 0;
                }
            }
        }

        return false;
    }

    private static bool HasInventoryTabRow(CaptureResult image, GridCandidate candidate)
    {
        var best = 0.0;
        var top = Math.Max(0, candidate.Y - 42);
        var bottom = Math.Max(0, candidate.Y - 10);
        var left = Math.Max(0, candidate.X - 38);
        var right = Math.Min(image.Width - 1, candidate.X + 12);

        for (var y = top; y <= bottom; y += 2)
        {
            for (var x = left; x <= right; x += 2)
            {
                var a = ScoreInventoryTabRect(image, x, y);
                var b = ScoreInventoryTabRect(image, x + 48, y);
                var c = ScoreInventoryTabRect(image, x + 96, y);
                var average = (a + b + c) / 3.0;
                var strong = (a >= 0.24 ? 1 : 0) + (b >= 0.24 ? 1 : 0) + (c >= 0.24 ? 1 : 0);
                if (strong >= 2)
                    best = Math.Max(best, average);
            }
        }

        return best >= 0.28;
    }

    private static double ScoreInventoryTabRect(CaptureResult image, int x, int y)
    {
        const int width = 42;
        const int height = 24;
        if (x < 0 || y < 0 || x + width >= image.Width || y + height >= image.Height)
            return 0;

        var total = 0;
        var tab = 0;
        for (var yy = y; yy < y + height; yy += 2)
        {
            for (var xx = x; xx < x + width; xx += 2)
            {
                var p = (yy * image.Width + xx) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                total++;
                if (LooksLikeInventoryTabPixel(r, g, b))
                    tab++;
            }
        }

        return total == 0 ? 0 : tab / (double)total;
    }

    private static double ScoreGridFrame(CaptureResult image, int x, int y, GridShape shape, bool full = false)
    {
        var samples = BuildSlotSamples(shape, full);
        var total = 0.0;
        var strong = 0;
        var count = 0;

        foreach (var (row, col) in samples)
        {
            var sx = x + col * (shape.CellWidth + shape.GapX);
            var sy = y + row * (shape.CellHeight + shape.GapY);
            var score = ScoreSlotFrame(image, sx, sy, shape.CellWidth, shape.CellHeight);
            total += score;
            if (score >= 0.42)
                strong++;
            count++;
        }

        if (count == 0)
            return 0;

        var average = total / count;
        var strongRatio = strong / (double)count;
        var frameScore = average * 0.70 + strongRatio * 0.30;
        var darkInterior = ScoreGridDarkInterior(image, x, y, shape, full);
        var panelSupport = ScorePanelSupport(image, x, y, shape);
        var titleSupport = ScoreTitleSupport(image, x, y, shape);
        var tabSupport = ScoreTabSupport(image, x, y, shape);
        var orangeFloorPenalty = ScoreOrangeFloorInterior(image, x, y, shape, full);
        return Math.Clamp(
            frameScore * 0.28
            + darkInterior * 0.10
            + panelSupport * 0.18
            + titleSupport * 0.14
            + tabSupport * 0.30
            - orangeFloorPenalty * 0.45,
            0,
            1);
    }

    private static double ScoreTabSupport(CaptureResult image, int x, int y, GridShape shape)
    {
        if (image.Width < 420 || image.Height < 320)
            return 0.65;

        var left = Math.Max(0, x - 10);
        var right = Math.Min(image.Width - 1, x + Math.Min(shape.TotalWidth, 180));
        var top = Math.Max(0, y - 48);
        var bottom = Math.Max(0, y - 6);
        if (right <= left || bottom <= top)
            return 0;

        var total = 0;
        var tab = 0;
        var rowRuns = 0;
        for (var yy = top; yy <= bottom; yy += 3)
        {
            var rowTotal = 0;
            var rowTab = 0;
            for (var xx = left; xx <= right; xx += 3)
            {
                var p = (yy * image.Width + xx) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                total++;
                rowTotal++;
                if (LooksLikeInventoryTabPixel(r, g, b))
                {
                    tab++;
                    rowTab++;
                }
            }

            if (rowTotal > 0 && rowTab / (double)rowTotal >= 0.20)
                rowRuns++;
        }

        if (total == 0)
            return 0;

        var density = tab / (double)total;
        var runScore = Math.Min(1, rowRuns / 5.0);
        return Math.Clamp(density * 2.5 + runScore * 0.35, 0, 1);
    }

    private static double ScoreGridDarkInterior(CaptureResult image, int x, int y, GridShape shape, bool full)
    {
        var samples = BuildSlotSamples(shape, full);
        var dark = 0;
        var total = 0;

        foreach (var (row, col) in samples)
        {
            var sx = x + col * (shape.CellWidth + shape.GapX) + 5;
            var sy = y + row * (shape.CellHeight + shape.GapY) + 5;
            var ex = Math.Min(image.Width, sx + Math.Max(1, shape.CellWidth - 10));
            var ey = Math.Min(image.Height, sy + Math.Max(1, shape.CellHeight - 10));
            var stepX = Math.Max(1, (ex - sx) / 4);
            var stepY = Math.Max(1, (ey - sy) / 4);

            for (var yy = sy; yy < ey; yy += stepY)
            {
                for (var xx = sx; xx < ex; xx += stepX)
                {
                    var p = (yy * image.Width + xx) * 4;
                    var b = image.Pixels[p];
                    var g = image.Pixels[p + 1];
                    var r = image.Pixels[p + 2];
                    var max = Math.Max(r, Math.Max(g, b));
                    var min = Math.Min(r, Math.Min(g, b));
                    total++;
                    if (max <= 72 || (max <= 105 && max - min <= 38))
                        dark++;
                }
            }
        }

        return total == 0 ? 0 : dark / (double)total;
    }

    private static double ScoreOrangeFloorInterior(CaptureResult image, int x, int y, GridShape shape, bool full)
    {
        var samples = BuildSlotSamples(shape, full);
        var orange = 0;
        var total = 0;

        foreach (var (row, col) in samples)
        {
            var sx = x + col * (shape.CellWidth + shape.GapX) + shape.CellWidth / 3;
            var sy = y + row * (shape.CellHeight + shape.GapY) + shape.CellHeight / 3;
            var ex = Math.Min(image.Width - 1, sx + shape.CellWidth / 3);
            var ey = Math.Min(image.Height - 1, sy + shape.CellHeight / 3);
            for (var yy = sy; yy <= ey; yy += Math.Max(1, shape.CellHeight / 6))
            {
                for (var xx = sx; xx <= ex; xx += Math.Max(1, shape.CellWidth / 6))
                {
                    var p = (yy * image.Width + xx) * 4;
                    var b = image.Pixels[p];
                    var g = image.Pixels[p + 1];
                    var r = image.Pixels[p + 2];
                    total++;
                    if (r >= 78 && g >= 36 && g <= 135 && b <= 72 && r > g + 18)
                        orange++;
                }
            }
        }

        return total == 0 ? 0 : orange / (double)total;
    }

    private static double ScorePanelSupport(CaptureResult image, int x, int y, GridShape shape)
    {
        var left = Math.Max(0, x - 12);
        var top = Math.Max(0, y - 36);
        var right = Math.Min(image.Width - 1, x + shape.TotalWidth + 12);
        var bottom = Math.Min(image.Height - 1, y + shape.TotalHeight + 56);
        var total = 0;
        var panel = 0;

        for (var yy = top; yy <= bottom; yy += 8)
        {
            SamplePanelPixel(image, left, yy, ref total, ref panel);
            SamplePanelPixel(image, right, yy, ref total, ref panel);
        }

        for (var xx = left; xx <= right; xx += 8)
        {
            SamplePanelPixel(image, xx, top, ref total, ref panel);
            SamplePanelPixel(image, xx, bottom, ref total, ref panel);
        }

        return total == 0 ? 0 : panel / (double)total;
    }

    private static double ScoreTitleSupport(CaptureResult image, int x, int y, GridShape shape)
    {
        if (image.Width < 420 || image.Height < 320)
            return 0.65;

        var left = Math.Max(0, x - 18);
        var right = Math.Min(image.Width - 1, x + shape.TotalWidth + 18);
        var top = Math.Max(0, y - 84);
        var bottom = Math.Max(0, y - 10);
        if (bottom <= top || right <= left)
            return 0;

        var total = 0;
        var title = 0;
        var horizontalRuns = 0;
        for (var yy = top; yy <= bottom; yy += 4)
        {
            var rowTitle = 0;
            var rowTotal = 0;
            for (var xx = left; xx <= right; xx += 4)
            {
                var p = (yy * image.Width + xx) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                total++;
                rowTotal++;
                if (LooksLikeInventoryTitlePixel(r, g, b))
                {
                    title++;
                    rowTitle++;
                }
            }

            if (rowTotal > 0 && rowTitle / (double)rowTotal >= 0.55)
                horizontalRuns++;
        }

        if (total == 0)
            return 0;

        var density = title / (double)total;
        var runScore = Math.Min(1, horizontalRuns / 3.0);
        return Math.Clamp(density * 0.70 + runScore * 0.30, 0, 1);
    }

    private static void SamplePanelPixel(CaptureResult image, int x, int y, ref int total, ref int panel)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            return;

        var p = (y * image.Width + x) * 4;
        var b = image.Pixels[p];
        var g = image.Pixels[p + 1];
        var r = image.Pixels[p + 2];
        total++;
        if (LooksLikeInventoryFrame(r, g, b) || (r <= 45 && g <= 45 && b <= 45))
            panel++;
    }

    private static IReadOnlyList<(int Row, int Col)> BuildSlotSamples(GridShape shape, bool full)
    {
        if (full || shape.Columns * shape.Rows <= 24)
        {
            var all = new List<(int, int)>(shape.Columns * shape.Rows);
            for (var row = 0; row < shape.Rows; row++)
            for (var col = 0; col < shape.Columns; col++)
                all.Add((row, col));
            return all;
        }

        var samples = new HashSet<(int, int)>
        {
            (0, 0),
            (0, shape.Columns - 1),
            (shape.Rows - 1, 0),
            (shape.Rows - 1, shape.Columns - 1),
            (shape.Rows / 2, shape.Columns / 2)
        };

        for (var row = 0; row < shape.Rows; row += Math.Max(1, shape.Rows / 4))
        {
            samples.Add((row, 0));
            samples.Add((row, shape.Columns - 1));
            samples.Add((row, shape.Columns / 2));
        }

        for (var col = 0; col < shape.Columns; col += Math.Max(1, shape.Columns / 4))
        {
            samples.Add((0, col));
            samples.Add((shape.Rows - 1, col));
            samples.Add((shape.Rows / 2, col));
        }

        return samples.ToList();
    }

    private static double ScoreSlotFrame(CaptureResult image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width >= image.Width || y + height >= image.Height)
            return 0;

        var total = 0;
        var frame = 0;
        var contrast = 0;
        SampleHorizontal(image, x, y, width, ref total, ref frame, ref contrast);
        SampleHorizontal(image, x, y + height - 1, width, ref total, ref frame, ref contrast);
        SampleVertical(image, x, y, height, ref total, ref frame, ref contrast);
        SampleVertical(image, x + width - 1, y, height, ref total, ref frame, ref contrast);

        if (total == 0)
            return 0;

        var frameRatio = frame / (double)total;
        var contrastRatio = contrast / (double)total;
        return frameRatio * 0.72 + contrastRatio * 0.28;
    }

    private static void SampleHorizontal(CaptureResult image, int x, int y, int width, ref int total, ref int frame, ref int contrast)
    {
        var step = Math.Max(1, width / 8);
        for (var xx = x; xx < x + width; xx += step)
            SampleFramePixel(image, xx, y, ref total, ref frame, ref contrast);
    }

    private static void SampleVertical(CaptureResult image, int x, int y, int height, ref int total, ref int frame, ref int contrast)
    {
        var step = Math.Max(1, height / 8);
        for (var yy = y; yy < y + height; yy += step)
            SampleFramePixel(image, x, yy, ref total, ref frame, ref contrast);
    }

    private static void SampleFramePixel(CaptureResult image, int x, int y, ref int total, ref int frame, ref int contrast)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height)
            return;

        var p = (y * image.Width + x) * 4;
        var b = image.Pixels[p];
        var g = image.Pixels[p + 1];
        var r = image.Pixels[p + 2];
        total++;

        if (LooksLikeInventoryFrame(r, g, b))
            frame++;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max - min > 42 || max > 100)
            contrast++;
    }

    private static bool LooksLikeInventoryFrame(byte r, byte g, byte b)
    {
        var gold = r >= 78 && g >= 52 && b <= 95 && r >= g;
        var darkFrame = r <= 55 && g <= 55 && b <= 55;
        var brightEdge = r >= 135 && g >= 100 && b <= 100;
        return gold || darkFrame || brightEdge;
    }

    private static bool LooksLikeInventoryTitlePixel(byte r, byte g, byte b)
    {
        var dark = r <= 58 && g <= 58 && b <= 58;
        var gold = r >= 78 && g >= 48 && b <= 86 && r >= g;
        var brightGold = r >= 125 && g >= 88 && b <= 88;
        return dark || gold || brightGold;
    }

    private static bool LooksLikeInventoryTabPixel(byte r, byte g, byte b)
    {
        var gold = r >= 92 && g >= 58 && b <= 95 && r >= g + 8;
        var brightGold = r >= 145 && g >= 105 && b <= 95;
        var darkPanel = r <= 42 && g <= 42 && b <= 48;
        return gold || brightGold || darkPanel;
    }

    private static bool LooksLikeInventoryGridLinePixel(byte r, byte g, byte b)
    {
        var gold = r >= 88 && g >= 45 && b <= 96 && r >= g;
        var brightGold = r >= 135 && g >= 85 && b <= 92;
        return gold || brightGold;
    }

    private static double ScoreSlot(CaptureResult image, int x, int y, int width, int height)
    {
        var startX = Math.Clamp(x + 3, 0, image.Width - 1);
        var startY = Math.Clamp(y + 3, 0, image.Height - 1);
        var endX = Math.Clamp(x + width - 3, startX + 1, image.Width);
        var endY = Math.Clamp(y + height - 3, startY + 1, image.Height);

        var total = 0;
        var vivid = 0;
        var bright = 0;
        var edges = 0;
        var lastLum = -1;

        for (var yy = startY; yy < endY; yy++)
        {
            for (var xx = startX; xx < endX; xx++)
            {
                var p = (yy * image.Width + xx) * 4;
                var b = image.Pixels[p];
                var g = image.Pixels[p + 1];
                var r = image.Pixels[p + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var lum = r + g + b;

                total++;
                if (max > 82 && max - min > 24)
                    vivid++;
                if (max > 120)
                    bright++;
                if (lastLum >= 0 && Math.Abs(lum - lastLum) > 95)
                    edges++;

                lastLum = lum;
            }
        }

        if (total <= 0)
            return 0;

        var vividScore = vivid / (double)total;
        var brightScore = bright / (double)total;
        var edgeScore = edges / (double)total;
        return vividScore * 0.62 + brightScore * 0.25 + edgeScore * 0.13;
    }

    private readonly record struct GridShape(int Columns, int Rows, int CellWidth, int CellHeight, int GapX, int GapY)
    {
        public int TotalWidth => Columns * CellWidth + Math.Max(0, Columns - 1) * GapX;
        public int TotalHeight => Rows * CellHeight + Math.Max(0, Rows - 1) * GapY;
    }

    private readonly record struct GridCandidate(int X, int Y, GridShape Shape, double Score, string Source = "scan");

    private readonly record struct HorizontalRun(int Y, int X1, int X2)
    {
        public int Width => X2 - X1 + 1;
    }
}
