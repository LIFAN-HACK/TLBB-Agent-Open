using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

namespace TlbbTargetSampler;

public partial class MainWindow : Window
{
    private readonly RuntimeLogService _log = new();
    private readonly WindowBindingService _binding;
    private readonly WindowCaptureService _capture;
    private readonly string _saveRoot;

    private BoundGameWindow? _window;
    private bool _draggingBind;
    private int _targetOffsetX;
    private int _targetOffsetY;
    private int _previewZoom = 1;
    private bool _pickingPetDeployPoint;
    private int? _petDeployPointX;
    private int? _petDeployPointY;

    public MainWindow()
    {
        InitializeComponent();

        _binding = new WindowBindingService(_log);
        _capture = new WindowCaptureService(_log);
        _saveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LongBanXing",
            "samples",
            "targets");
        Directory.CreateDirectory(_saveRoot);

        SaveRootText.Text = _saveRoot;
        PetDeployProfileText.Text = GuessDefaultProfileName();
        UpdateCoverageText(TargetProtectionIndexBuilder.AnalyzeCoverage(_saveRoot));
        _log.Appended += (_, entry) => Dispatcher.Invoke(() => AppendLog(entry.Message));
        AppendLog("采集器已启动。");
    }

    private void AutoBind_Click(object sender, RoutedEventArgs e)
    {
        BindWindow(_binding.BindFirstGameWindow());
    }

    private void DragBindBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingBind = true;
        DragBindBox.CaptureMouse();
        AppendLog("拖到游戏窗口后松开鼠标。");
    }

    private void DragBindBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingBind)
            return;

        _draggingBind = false;
        DragBindBox.ReleaseMouseCapture();

        var screen = PointToScreen(e.GetPosition(this));
        BindWindow(_binding.BindByScreenPoint(screen.X, screen.Y));
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e)
    {
        RefreshPreview();
    }

    private void OpenSampleRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_saveRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = _saveRoot,
            UseShellExecute = true
        });
    }

    private void PetDeployPick_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            MessageBox.Show(this, "请先绑定游戏窗口并刷新预览。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pickingPetDeployPoint = true;
        PetDeployPickButton.Content = "请在右侧预览图点选";
        PetDeployPointText.Text = "等待点选：请点击预览图中的当前宠物出战按钮中心。";
        AppendLog("宠物出战点标定已开启：请在预览图上点选，不会点击游戏。");
    }

    private void PreviewImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_pickingPetDeployPoint)
            return;

        if (PreviewImage.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            AppendLog("无法标定：预览图为空。");
            return;
        }

        var position = e.GetPosition(PreviewImage);
        var scaleX = PreviewImage.ActualWidth > 0 ? PreviewImage.ActualWidth / source.PixelWidth : _previewZoom;
        var scaleY = PreviewImage.ActualHeight > 0 ? PreviewImage.ActualHeight / source.PixelHeight : _previewZoom;
        if (scaleX <= 0) scaleX = 1;
        if (scaleY <= 0) scaleY = 1;

        var x = Math.Clamp((int)Math.Round(position.X / scaleX), 0, source.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Round(position.Y / scaleY), 0, source.PixelHeight - 1);
        _petDeployPointX = x;
        _petDeployPointY = y;
        _pickingPetDeployPoint = false;
        PetDeployPickButton.Content = "重新点选";
        PetDeployPointText.Text = $"已选择：x={x}, y={y}, bottom={source.PixelHeight - y}";
        AppendLog($"宠物出战点已选择：x={x}, y={y}, bottom={source.PixelHeight - y}。");
        RefreshPreview();
        e.Handled = true;
    }

    private void SavePetDeployPoint_Click(object sender, RoutedEventArgs e)
    {
        if (_petDeployPointX is null || _petDeployPointY is null || PreviewImage.Source is not BitmapSource source)
        {
            MessageBox.Show(this, "请先在预览图中点选宠物出战按钮中心。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profileName = PetDeployProfileText.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
        {
            MessageBox.Show(this, "方案名不能为空。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var bottom = source.PixelHeight - _petDeployPointY.Value;
        try
        {
            var result = SavePetDeployPoint(profileName, _petDeployPointX.Value, bottom);
            AppendLog($"宠物出战点已保存：{profileName} -> x={_petDeployPointX}, bottom={bottom}。备份：{result.BackupPath}");
            MessageBox.Show(this, $"已保存到方案：{profileName}\n点击点：x={_petDeployPointX}, bottom={bottom}\n备份：{result.BackupPath}", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"宠物出战点保存失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string value || !int.TryParse(value, out var zoom))
            return;

        _previewZoom = Math.Clamp(zoom, 1, 8);
        PreviewScaleTransform.ScaleX = _previewZoom;
        PreviewScaleTransform.ScaleY = _previewZoom;
        UpdatePreviewInfo();
    }

    private void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (_window is null)
        {
            MessageBox.Show(this, "请先绑定游戏窗口。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var capture = _capture.CaptureClient(_window.Hwnd);
        if (capture is null)
        {
            MessageBox.Show(this, "截图失败，请确认游戏窗口仍然存在。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var coverage = TargetProtectionIndexBuilder.AnalyzeCoverage(_saveRoot);
        var dialog = new LabelDialog(BuildMissingGuidance(coverage), RecommendNextKind(coverage)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            AppendLog("已取消采集。");
            return;
        }

        try
        {
            var precheck = AnalyzeCaptureForSamplePrecheck(_window, capture, dialog.ExpectedKind);
            if (precheck.RiskReasons.Count > 0)
            {
                var message = "保存前识别预检发现风险：" + Environment.NewLine
                              + string.Join(Environment.NewLine, precheck.RiskReasons.Select(r => $"· {r}"))
                              + Environment.NewLine + Environment.NewLine
                              + "继续保存会把这份样本标记为待复核。确认继续吗？";
                var confirm = MessageBox.Show(
                    this,
                    message,
                    "样本预检提醒",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    AppendLog("已取消保存：预检风险未确认。");
                    return;
                }
            }

            var result = SaveSample(_window, capture, dialog.Label, dialog.ExpectedKind, precheck);
            var indexResult = TargetProtectionIndexBuilder.Rebuild(_saveRoot);
            UpdateCoverageText(indexResult.Coverage);
            LastSavedText.Text = $"{result.Directory}{Environment.NewLine}索引：{indexResult.OutputPath}";
            PreviewImage.Source = LoadBitmap(result.OverlayPath);
            UpdatePreviewInfo(capture.Width, capture.Height);
            AppendLog($"预检：{precheck.Summary}");
            AppendLog(indexResult.Message);
            AppendLog($"已保存样本：{result.Directory}");
        }
        catch (Exception ex)
        {
            AppendLog($"保存失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string direction)
            return;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        switch (direction)
        {
            case "left":
                _targetOffsetX -= step;
                break;
            case "right":
                _targetOffsetX += step;
                break;
            case "up":
                _targetOffsetY -= step;
                break;
            case "down":
                _targetOffsetY += step;
                break;
            case "reset":
                _targetOffsetX = 0;
                _targetOffsetY = 0;
                break;
        }

        OffsetText.Text = $"偏移：X {_targetOffsetX} / Y {_targetOffsetY}";
        RefreshPreview();
    }

    private void BindWindow(BoundGameWindow? window)
    {
        if (window is null)
        {
            AppendLog("绑定失败。");
            return;
        }

        _window = window;
        WindowTitleText.Text = window.Title;
        WindowInfoText.Text = $"PID {window.Pid} / {window.Bitness} / {window.ClientSizeText} / {window.Id}";
        WindowPathText.Text = window.ProcessPath;
        AppendLog($"已绑定：{window.Title}，{window.ClientSizeText}。");
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_window is null)
        {
            AppendLog("未绑定窗口。");
            return;
        }

        var capture = _capture.CaptureClient(_window.Hwnd);
        if (capture is null)
        {
            AppendLog("预览截图失败。");
            return;
        }

        PreviewImage.Source = CreateOverlayBitmap(capture, ResolveRegions(capture.Width, capture.Height, _targetOffsetX, _targetOffsetY), _petDeployPointX, _petDeployPointY);
        UpdatePreviewInfo(capture.Width, capture.Height);
        AppendLog("预览已刷新。");
    }

    private void UpdatePreviewInfo(int? width = null, int? height = null)
    {
        var size = width is > 0 && height is > 0
            ? $"{width}x{height}"
            : PreviewInfoText.Text.Split('|').FirstOrDefault()?.Trim();

        PreviewInfoText.Text = string.IsNullOrWhiteSpace(size)
            ? $"缩放 {_previewZoom}x"
            : $"{size} | 缩放 {_previewZoom}x";
    }

    private SampleRecognitionPrecheck AnalyzeCaptureForSamplePrecheck(BoundGameWindow window, CaptureResult capture, string expectedKind)
    {
        var riskReasons = new List<string>();
        try
        {
            var ocr = new TemplateOcrService(_log);
            var runtimeData = new RuntimeDataService(_log);
            var dictionaryFiles = runtimeData.EnumerateDictionaryFiles();
            foreach (var dictionary in dictionaryFiles)
            {
                try
                {
                    ocr.LoadDictionary(dictionary);
                }
                catch (Exception ex)
                {
                    AppendLog($"字库跳过：{Path.GetFileName(dictionary)}，{ex.Message}");
                }
            }

            var global = new GlobalConfigService(new SamplerMemoryProfileService(), _log);
            var recognition = new CombatRecognitionService(
                new StaticClientCaptureService(capture),
                ocr,
                _log,
                targetSamples: new TargetProtectionSampleService(_log),
                global: global);
            var combat = recognition.Recognize(window, global.Current.CombatRecognition);
            riskReasons.AddRange(BuildSamplePrecheckRiskReasons(expectedKind, combat));

            var summary = BuildSamplePrecheckSummary(combat, ocr, riskReasons);
            return new SampleRecognitionPrecheck(
                CheckedAt: DateTime.Now.ToString("O"),
                ExpectedKind: NormalizeKind(expectedKind),
                Summary: summary,
                HasTarget: combat.HasTarget,
                TargetNameText: combat.TargetNameText,
                TargetHpPercent: combat.TargetHpPercent,
                PlayerHpPercent: combat.PlayerHpPercent,
                PlayerMpPercent: combat.PlayerMpPercent,
                PetHpPercent: combat.PetHpPercent,
                TargetLooksLikePlayer: combat.TargetLooksLikePlayer,
                TargetProtectionAmbiguous: combat.TargetProtectionAmbiguous,
                TargetLooksLikePet: combat.TargetLooksLikePet,
                TargetIsAttackable: combat.TargetIsAttackable,
                TargetSampleKind: combat.TargetSampleKind,
                TargetSampleLabel: combat.TargetSampleLabel,
                TargetSampleScore: combat.TargetSampleScore,
                DictionaryFileCount: dictionaryFiles.Count,
                LoadedGlyphCount: ocr.LoadedGlyphCount,
                RenderableGlyphCount: ocr.RenderableGlyphCount,
                RiskReasons: riskReasons);
        }
        catch (Exception ex)
        {
            riskReasons.Add($"预检失败：{ex.Message}");
            return new SampleRecognitionPrecheck(
                CheckedAt: DateTime.Now.ToString("O"),
                ExpectedKind: NormalizeKind(expectedKind),
                Summary: "预检失败，样本需人工复核。",
                HasTarget: false,
                TargetNameText: "",
                TargetHpPercent: null,
                PlayerHpPercent: null,
                PlayerMpPercent: null,
                PetHpPercent: null,
                TargetLooksLikePlayer: false,
                TargetProtectionAmbiguous: false,
                TargetLooksLikePet: false,
                TargetIsAttackable: null,
                TargetSampleKind: "",
                TargetSampleLabel: "",
                TargetSampleScore: 0,
                DictionaryFileCount: 0,
                LoadedGlyphCount: 0,
                RenderableGlyphCount: 0,
                RiskReasons: riskReasons);
        }
    }

    private static IReadOnlyList<string> BuildSamplePrecheckRiskReasons(string expectedKind, CombatRecognitionResult combat)
    {
        var reasons = new List<string>();
        var kind = NormalizeKind(expectedKind);
        switch (kind)
        {
            case "monster":
            case "boss":
            case "elite":
                if (!combat.HasTarget)
                    reasons.Add("你选择的是可攻击目标，但预检未识别到目标面板。");
                if (combat.TargetLooksLikePlayer)
                    reasons.Add("预检命中玩家目标特征，不能直接作为怪物/BOSS放行样本。");
                if (combat.TargetProtectionAmbiguous)
                    reasons.Add("预检命中门派竖牌或头像结构高风险，需人工确认不是玩家。");
                if (combat.TargetLooksLikePet)
                    reasons.Add("预检命中宠物/宝宝目标特征，不能直接作为怪物/BOSS放行样本。");
                break;
            case "player":
                if (!combat.TargetLooksLikePlayer && !combat.TargetProtectionAmbiguous)
                    reasons.Add("你选择的是玩家目标，但预检未命中玩家特征；请确认目标面板是否完整。");
                if (combat.TargetLooksLikePet)
                    reasons.Add("预检命中宠物/宝宝目标特征，请确认是否误选。");
                break;
            case "pet":
                if (combat.TargetLooksLikePlayer || combat.TargetProtectionAmbiguous)
                    reasons.Add("你选择的是宠物/宝宝，但预检命中玩家保护特征。");
                if (!combat.TargetLooksLikePet)
                    reasons.Add("你选择的是宠物/宝宝，但预检未命中宠物样本特征；当前仍需人工复核。");
                break;
            case "none":
                if (combat.HasTarget)
                    reasons.Add("你选择的是无目标，但预检检测到目标面板或目标血条。");
                break;
        }

        return reasons;
    }

    private static string BuildSamplePrecheckSummary(CombatRecognitionResult combat, ITemplateOcrService ocr, IReadOnlyList<string> riskReasons)
    {
        var kind = string.IsNullOrWhiteSpace(combat.TargetSampleKind)
            ? "未命中样本"
            : $"{combat.TargetSampleKind}/{combat.TargetSampleScore:0.000}";
        var target = combat.HasTarget ? "有目标" : "无目标";
        var flags = new List<string>();
        if (combat.TargetLooksLikePlayer)
            flags.Add("玩家");
        if (combat.TargetProtectionAmbiguous)
            flags.Add("玩家高风险");
        if (combat.TargetLooksLikePet)
            flags.Add("宠物");
        if (flags.Count == 0)
            flags.Add("未命中保护特征");

        var risk = riskReasons.Count == 0 ? "无风险" : $"风险 {riskReasons.Count} 项";
        return $"{target}，目标血={FormatPct(combat.TargetHpPercent)}，人物血={FormatPct(combat.PlayerHpPercent)}，人物蓝={FormatPct(combat.PlayerMpPercent)}，宠物血={FormatPct(combat.PetHpPercent)}，样本={kind}，标记={string.Join("/", flags)}，字库={ocr.RenderableGlyphCount}，{risk}";
    }

    private static string FormatPct(int? value) => value.HasValue ? $"{value.Value}%" : "-";

    private static string NormalizeKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "player" or "monster" or "boss" or "elite" or "pet" or "none" or "unknown"
            ? normalized
            : "unknown";
    }

    private SampleSaveResult SaveSample(BoundGameWindow window, CaptureResult capture, string label, string expectedKind, SampleRecognitionPrecheck precheck)
    {
        var timestamp = DateTime.Now;
        var safeLabel = SanitizeFileName(label);
        var directory = Path.Combine(_saveRoot, $"{timestamp:yyyyMMdd-HHmmssfff}_{safeLabel}");
        var cropsDir = Path.Combine(directory, "crops");
        Directory.CreateDirectory(cropsDir);

        var regions = ResolveRegions(capture.Width, capture.Height, _targetOffsetX, _targetOffsetY);
        var clientPath = Path.Combine(directory, "client.png");
        var overlayPath = Path.Combine(directory, "overlay.png");
        SaveBitmap(ToBitmapSource(capture), clientPath);
        SaveBitmap(CreateOverlayBitmap(capture, regions), overlayPath);

        var cropFiles = new List<CropMetadata>();
        foreach (var region in regions)
        {
            var crop = Crop(capture, region.Region);
            if (crop is null)
                continue;

            var fileName = $"{region.Key}_{region.Region.X}_{region.Region.Y}_{region.Region.Width}x{region.Region.Height}.png";
            var path = Path.Combine(cropsDir, fileName);
            var bitmap = ToBitmapSource(crop);
            SaveBitmap(bitmap, path);
            var canonicalName = TargetProtectionIndexBuilder.CanonicalCropFileName(region.Key);
            if (!string.IsNullOrWhiteSpace(canonicalName))
                SaveBitmap(bitmap, Path.Combine(cropsDir, canonicalName));
            cropFiles.Add(new CropMetadata(region.Key, region.Title, fileName, region.Region));
        }

        var metadata = new TargetSampleMetadata(
            SchemaVersion: 3,
            CreatedAt: timestamp.ToString("O"),
            Label: label,
            ExpectedKind: expectedKind,
            Window: new WindowMetadata(
                Hwnd: window.Id,
                Pid: window.Pid,
                Title: window.Title,
                ProcessName: window.ProcessName,
                ProcessPath: window.ProcessPath,
                Bitness: window.Bitness.ToString(),
                ClientWidth: capture.Width,
                ClientHeight: capture.Height),
            TargetOffset: new TargetOffsetMetadata(_targetOffsetX, _targetOffsetY),
            Files: new SampleFiles("client.png", "overlay.png", "crops"),
            Regions: cropFiles,
            RecognitionPrecheck: precheck);

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(Path.Combine(directory, "metadata.json"), json);

        return new SampleSaveResult(directory, overlayPath);
    }

    private static IReadOnlyList<RegionDefinition> ResolveRegions(int clientWidth, int clientHeight, int targetOffsetX, int targetOffsetY)
    {
        var config = new CombatRecognitionConfig();
        var targetName = Offset(OcrRegionProjection.Resolve(config.TargetName, clientWidth, clientHeight), targetOffsetX, targetOffsetY, clientWidth, clientHeight);
        var targetHp = Offset(OcrRegionProjection.Resolve(config.TargetHp, clientWidth, clientHeight), targetOffsetX, targetOffsetY, clientWidth, clientHeight);
        var targetMp = Offset(OcrRegionProjection.Resolve(config.TargetMp, clientWidth, clientHeight), targetOffsetX, targetOffsetY, clientWidth, clientHeight);
        var targetRage = Offset(OcrRegionProjection.Resolve(config.TargetRage, clientWidth, clientHeight), targetOffsetX, targetOffsetY, clientWidth, clientHeight);
        var playerHp = OcrRegionProjection.Resolve(config.PlayerHp, clientWidth, clientHeight);
        var playerMp = OcrRegionProjection.Resolve(config.PlayerMp, clientWidth, clientHeight);
        var petHp = OcrRegionProjection.Resolve(config.PetHp, clientWidth, clientHeight);

        var targetStatus = Clamp(new OcrResolvedRegion(
            targetHp.X,
            targetHp.Y,
            targetHp.Width,
            Math.Min(26, clientHeight - targetHp.Y)), clientWidth, clientHeight);

        var playerRage = Clamp(new OcrResolvedRegion(
            playerHp.X,
            playerHp.Y + 12,
            playerHp.Width,
            6), clientWidth, clientHeight);

        var schoolMarker = Clamp(new OcrResolvedRegion(
            Math.Max(0, targetHp.X - 36),
            Math.Max(0, targetHp.Y - 24),
            36,
            82), clientWidth, clientHeight);

        var targetPortrait = Clamp(new OcrResolvedRegion(
            Math.Min(clientWidth - 1, targetHp.X + targetHp.Width + 10),
            Math.Max(0, targetHp.Y - 24),
            88,
            88), clientWidth, clientHeight);

        return new[]
        {
            new RegionDefinition("target_name", "目标名字", targetName, Colors.White),
            new RegionDefinition("target_hp", "目标血条", targetHp, Colors.Red),
            new RegionDefinition("target_mp", "目标蓝条", targetMp, Colors.DeepSkyBlue),
            new RegionDefinition("target_rage", "目标怒气", targetRage, Colors.Gold),
            new RegionDefinition("target_status", "玩家条判定", targetStatus, Colors.Cyan),
            new RegionDefinition("school_marker", "门派竖牌", schoolMarker, Colors.Gold),
            new RegionDefinition("target_portrait", "目标头像", targetPortrait, Colors.Magenta),
            new RegionDefinition("player_hp", "人物血条", playerHp, Colors.Lime),
            new RegionDefinition("player_mp", "人物蓝条", playerMp, Colors.DeepSkyBlue),
            new RegionDefinition("player_rage", "人物怒气", playerRage, Colors.Gold),
            new RegionDefinition("pet_hp", "宠物血条", petHp, Colors.Orange)
        };
    }

    private static OcrResolvedRegion Offset(OcrResolvedRegion region, int dx, int dy, int width, int height)
    {
        if (region.IsEmpty)
            return region;

        return Clamp(new OcrResolvedRegion(region.X + dx, region.Y + dy, region.Width, region.Height), width, height);
    }

    private static OcrResolvedRegion Clamp(OcrResolvedRegion region, int width, int height)
    {
        var x = Math.Clamp(region.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(region.Y, 0, Math.Max(0, height - 1));
        var w = Math.Min(region.Width, width - x);
        var h = Math.Min(region.Height, height - y);
        return new OcrResolvedRegion(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private static CaptureResult? Crop(CaptureResult source, OcrResolvedRegion region)
    {
        if (region.IsEmpty)
            return null;

        var x = Math.Max(0, region.X);
        var y = Math.Max(0, region.Y);
        var w = Math.Min(region.Width, source.Width - x);
        var h = Math.Min(region.Height, source.Height - y);
        if (w <= 0 || h <= 0)
            return null;

        var dst = new byte[w * h * 4];
        var srcStride = source.Width * 4;
        var dstStride = w * 4;
        for (var row = 0; row < h; row++)
            Array.Copy(source.Pixels, (y + row) * srcStride + x * 4, dst, row * dstStride, dstStride);

        return new CaptureResult(w, h, dst);
    }

    private static BitmapSource ToBitmapSource(CaptureResult capture)
    {
        var bitmap = BitmapSource.Create(
            capture.Width,
            capture.Height,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            capture.Pixels,
            capture.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateOverlayBitmap(CaptureResult capture, IReadOnlyList<RegionDefinition> regions, int? petDeployX = null, int? petDeployY = null)
    {
        var source = ToBitmapSource(capture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, capture.Width, capture.Height));
            foreach (var region in regions.Where(r => !r.Region.IsEmpty))
            {
                var brush = new SolidColorBrush(region.Color);
                brush.Freeze();
                var pen = new Pen(brush, 2);
                pen.Freeze();
                var rect = new Rect(region.Region.X, region.Region.Y, region.Region.Width, region.Region.Height);
                dc.DrawRectangle(null, pen, rect);
            }

            if (petDeployX is int x && petDeployY is int y)
            {
                var brush = Brushes.LawnGreen;
                var pen = new Pen(brush, 3);
                pen.Freeze();
                dc.DrawEllipse(null, pen, new Point(x, y), 12, 12);
                dc.DrawLine(pen, new Point(Math.Max(0, x - 22), y), new Point(Math.Min(capture.Width, x + 22), y));
                dc.DrawLine(pen, new Point(x, Math.Max(0, y - 22)), new Point(x, Math.Min(capture.Height, y + 22)));
            }
        }

        var render = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        render.Freeze();
        return render;
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string SanitizeFileName(string label)
    {
        var value = Regex.Replace(label.Trim(), @"[\\/:*?""<>|\s]+", "_");
        value = value.Trim('_');
        return string.IsNullOrWhiteSpace(value) ? "unlabeled" : value[..Math.Min(value.Length, 32)];
    }

    private static string GuessDefaultProfileName()
    {
        var roots = ProfileRoots();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            var preferred = Path.Combine(root, "方案-0621-0916.json");
            if (File.Exists(preferred))
                return "方案-0621-0916";

            var file = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith("return-points-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(file))
                return Path.GetFileNameWithoutExtension(file);
        }

        return "方案-0621-0916";
    }

    private static IReadOnlyList<string> ProfileRoots()
    {
        var localizedAppName = string.Concat((char)0x9F99, (char)0x4F34, (char)0x884C);
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), localizedAppName, "profiles"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongBanXing", "profiles")
        };
    }

    private static PetDeploySaveResult SavePetDeployPoint(string profileName, int x, int bottom)
    {
        if (x <= 0 || bottom <= 0)
            throw new InvalidOperationException("宠物出战点击点无效，x 和 bottom 必须大于 0。");

        var profilePath = ProfileRoots()
            .Select(root => Path.Combine(root, profileName + ".json"))
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(profilePath))
            throw new FileNotFoundException($"找不到方案文件：{profileName}");

        var root = JsonNode.Parse(File.ReadAllText(profilePath))?.AsObject()
                   ?? throw new InvalidOperationException("方案 JSON 无法解析。");

        var backupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LongBanXing",
            "backups",
            "pet-deploy");
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(backupRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(profilePath)}");
        File.Copy(profilePath, backupPath, overwrite: true);

        root["PetDeployActivationMode"] = (int)PetSkillActivationMode.ClientClick;
        root["PetDeployClickX"] = x;
        root["PetDeployClickBottomOffset"] = bottom;
        root["PetDeployHotkey"] = "";
        if (root["PetDeployCooldownMs"] is null || root["PetDeployCooldownMs"]!.GetValue<int>() <= 0)
            root["PetDeployCooldownMs"] = 15000;

        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(profilePath, json, new System.Text.UTF8Encoding(false));
        return new PetDeploySaveResult(profilePath, backupPath);
    }

    private void UpdateCoverageText(TargetSampleCoverageResult coverage)
    {
        CoverageText.Text = BuildCoverageSummary(coverage);
        NextSampleText.Text = BuildNextSampleHint(coverage);
    }

    private static string BuildCoverageSummary(TargetSampleCoverageResult coverage)
    {
        var state = coverage.RecommendedReady
            ? "推荐覆盖已满足"
            : coverage.MinimalReady
                ? "最小链路可用，仍需补样本"
                : "样本不足";
        return $"样本覆盖：{state} | 总数 {coverage.Total}，玩家 {coverage.Player}，普通怪 {coverage.Monster}，BOSS/首领 {coverage.BossOrElite}，宠物 {coverage.Pet}，无目标 {coverage.None}，未知 {coverage.Unknown}。{BuildMissingGuidance(coverage)}";
    }

    private static string BuildMissingGuidance(TargetSampleCoverageResult coverage)
    {
        var missing = new List<string>();
        if (coverage.Player < 3)
            missing.Add($"玩家还需 {3 - coverage.Player} 个");
        if (coverage.Monster < 3)
            missing.Add($"普通怪还需 {3 - coverage.Monster} 个");
        if (coverage.BossOrElite < 2)
            missing.Add($"BOSS/首领还需 {2 - coverage.BossOrElite} 个");
        if (coverage.Pet < 1)
            missing.Add("宠物/宝宝还需 1 个");
        if (coverage.None < 1)
            missing.Add("无目标还需 1 个");
        if (coverage.NotReadyForTemplate > 0)
            missing.Add($"裁剪不完整样本 {coverage.NotReadyForTemplate} 个");

        return missing.Count == 0
            ? "当前缺口：无。"
            : "当前缺口：" + string.Join("；", missing) + "。";
    }

    private static string BuildNextSampleHint(TargetSampleCoverageResult coverage)
    {
        var kind = RecommendNextKind(coverage);
        return kind == "unknown"
            ? "下一份建议：推荐覆盖已满足，可只采异常样本。"
            : $"下一份建议：优先采集【{KindDisplayName(kind)}】。";
    }

    private static string RecommendNextKind(TargetSampleCoverageResult coverage)
    {
        if (coverage.Pet < 1)
            return "pet";
        if (coverage.None < 1)
            return "none";
        if (coverage.Player < 3)
            return "player";
        if (coverage.BossOrElite < 2)
            return "boss";
        if (coverage.Monster < 3)
            return "monster";

        return "unknown";
    }

    private static string KindDisplayName(string kind) => kind switch
    {
        "player" => "玩家目标",
        "monster" => "普通怪物",
        "boss" => "首领/BOSS",
        "elite" => "首领/BOSS",
        "pet" => "宠物/宝宝",
        "none" => "无目标",
        _ => "待复核"
    };

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogList.Items.Add(line);
        LogList.ScrollIntoView(line);
    }

    private sealed record RegionDefinition(string Key, string Title, OcrResolvedRegion Region, Color Color);
    private sealed record SampleSaveResult(string Directory, string OverlayPath);
    private sealed record PetDeploySaveResult(string ProfilePath, string BackupPath);
    private sealed record SampleFiles(string Client, string Overlay, string CropsDirectory);
    private sealed record WindowMetadata(string Hwnd, int Pid, string Title, string ProcessName, string ProcessPath, string Bitness, int ClientWidth, int ClientHeight);
    private sealed record TargetOffsetMetadata(int X, int Y);
    private sealed record CropMetadata(string Key, string Title, string FileName, OcrResolvedRegion Region);
    private sealed record SampleRecognitionPrecheck(
        string CheckedAt,
        string ExpectedKind,
        string Summary,
        bool HasTarget,
        string TargetNameText,
        int? TargetHpPercent,
        int? PlayerHpPercent,
        int? PlayerMpPercent,
        int? PetHpPercent,
        bool TargetLooksLikePlayer,
        bool TargetProtectionAmbiguous,
        bool TargetLooksLikePet,
        bool? TargetIsAttackable,
        string TargetSampleKind,
        string TargetSampleLabel,
        double TargetSampleScore,
        int DictionaryFileCount,
        int LoadedGlyphCount,
        int RenderableGlyphCount,
        IReadOnlyList<string> RiskReasons);
    private sealed record TargetSampleMetadata(int SchemaVersion, string CreatedAt, string Label, string ExpectedKind, WindowMetadata Window, TargetOffsetMetadata TargetOffset, SampleFiles Files, IReadOnlyList<CropMetadata> Regions, SampleRecognitionPrecheck RecognitionPrecheck);

    private sealed class SamplerMemoryProfileService : IProfileService
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        public string? CurrentProfileName { get; private set; }

        public IReadOnlyList<string> ListProfileNames() =>
            _store.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        public void Save(string name, string jsonPayload)
        {
            _store[name] = jsonPayload;
            CurrentProfileName = name;
        }

        public string? Load(string name)
        {
            CurrentProfileName = name;
            return _store.TryGetValue(name, out var value) ? value : null;
        }

        public void Delete(string name)
        {
            _store.Remove(name);
            if (CurrentProfileName == name)
                CurrentProfileName = null;
        }
    }

    private sealed class StaticClientCaptureService(CaptureResult capture) : IWindowCaptureService
    {
        public CaptureResult? CaptureClient(nint hwnd) => capture;

        public CaptureResult? CaptureRegion(nint hwnd, int x, int y, int w, int h) =>
            CropRect(capture, x, y, w, h);

        public CaptureResult? CaptureAvatar(nint hwnd) => null;

        private static CaptureResult? CropRect(CaptureResult source, int x, int y, int width, int height)
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
    }
}
