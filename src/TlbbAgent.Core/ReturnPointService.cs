using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ReturnPointService : IReturnPointService
{
    private const string Module = "ReturnPoint";
    private const string ProfilePrefix = "return-points-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static ReturnPointService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ReturnPointService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public ReturnPointConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<string> MapNames =>
        Current.Points
            .Select(p => p.MapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    public void NotifyChanged()
    {
        Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddPoint(ReturnPoint point)
    {
        if (string.IsNullOrWhiteSpace(point.MapName))
            throw new ArgumentException("Map name cannot be empty.", nameof(point));
        if (point.X < 0 || point.Y < 0)
            throw new ArgumentException("Coordinates cannot be negative.", nameof(point));

        Current.Points.Add(point);
        ReconcileConfirmedMapNameFromConfiguredMaps(allowAutoFillWhenBlank: true);
        _log.Info(Module, $"已添加返点坐标：{point.MapName} {point.X},{point.Y} [{point.Kind}]");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePoint(string id)
    {
        var point = Current.Points.FirstOrDefault(p => p.Id == id);
        if (point is null)
            return false;

        Current.Points.Remove(point);
        ReconcileConfirmedMapNameFromConfiguredMaps(allowAutoFillWhenBlank: true);
        _log.Info(Module, $"已删除返点坐标：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        Current.Points.Clear();
        if (Current.NavigationRecognition.ConfirmedMapNameAutoFilled)
        {
            Current.NavigationRecognition.ConfirmedMapName = "";
            Current.NavigationRecognition.ConfirmedMapNameAutoFilled = false;
        }
        _log.Info(Module, "已清空返点坐标。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ReturnPointImportResult ImportLegacyIni(string path, string sourceModule)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Coordinate file does not exist.", fullPath);

        var mapCount = 0;
        var pointCount = 0;
        var skipped = 0;
        var inZb = false;

        foreach (var raw in ReadAllLines(fullPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inZb = line.Equals("[zb]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inZb)
                continue;

            var split = line.Split('=', 2);
            if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]))
            {
                skipped++;
                continue;
            }

            var mapName = split[0].Trim();
            var coords = split[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var addedForMap = 0;

            foreach (var coord in coords)
            {
                var xy = coord.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
                {
                    skipped++;
                    continue;
                }

                Current.Points.Add(new ReturnPoint
                {
                    MapName = mapName,
                    X = x,
                    Y = y,
                    Label = $"{mapName}-{addedForMap + 1}",
                    Kind = ReturnPointKind.FixedFarm,
                    SourceModule = sourceModule,
                    SourcePath = ""
                });
                addedForMap++;
                pointCount++;
            }

        if (addedForMap > 0)
                mapCount++;
        }

        var result = new ReturnPointImportResult(fullPath, mapCount, pointCount, skipped);
        ReconcileConfirmedMapNameFromConfiguredMaps(allowAutoFillWhenBlank: true);
        _log.Info(Module, $"坐标已载入：地图 {mapCount} | 坐标 {pointCount} | 跳过 {skipped}");
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Save(string profileName)
    {
        Normalize(Current);
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"返点配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<ReturnPointConfig>(json);
            if (cfg is null)
                return false;

            Current = cfg;
            Normalize(Current);
            ReconcileConfirmedMapNameFromConfiguredMaps(allowAutoFillWhenBlank: true);
            _log.Info(Module, $"返点配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            return SplitLines(utf8.GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void Normalize(ReturnPointConfig cfg)
    {
        cfg.CheckIntervalMs = Math.Clamp(cfg.CheckIntervalMs <= 0 ? 1000 : cfg.CheckIntervalMs, 200, 60000);
        cfg.ArrivalDistance = Math.Clamp(cfg.ArrivalDistance <= 0 ? 8 : cfg.ArrivalDistance, 1, 100);
        cfg.StuckTimeoutMs = Math.Clamp(cfg.StuckTimeoutMs <= 0 ? 15000 : cfg.StuckTimeoutMs, 1000, 300000);
        cfg.ReturnRetryLimit = Math.Clamp(cfg.ReturnRetryLimit <= 0 ? 3 : cfg.ReturnRetryLimit, 1, 20);
        cfg.ReturnTravelWaitMs = Math.Clamp(cfg.ReturnTravelWaitMs <= 0 ? 8000 : cfg.ReturnTravelWaitMs, 500, 60000);

        cfg.NavigationRecognition ??= new NavigationRecognitionConfig();
        cfg.NavigationRecognition.MapName ??= new OcrRegionConfig { X = 2365, Y = 15, Width = 105, Height = 20 };
        if (IsLegacyDefaultMapNameRegion(cfg.NavigationRecognition.MapName))
            cfg.NavigationRecognition.MapName.Width = 105;
        cfg.NavigationRecognition.Coordinate ??= new OcrRegionConfig { X = 2368, Y = 31, Width = 120, Height = 18 };
        cfg.DeathRecognition ??= new DeathRecognitionConfig();
        cfg.DeathRecognition.PromptRegion ??= new OcrRegionConfig { X = 360, Y = 250, Width = 360, Height = 120 };
        cfg.ReturnNavigation ??= new ReturnNavigationConfig();
        cfg.EarthSpiritNavigation ??= new ReturnNavigationConfig();
        cfg.StuckRecoveryNavigation ??= new ReturnNavigationConfig();
        cfg.DeathReturnNavigation ??= new ReturnNavigationConfig();
        NormalizeNavigation(cfg.ReturnNavigation);
        NormalizeNavigation(cfg.EarthSpiritNavigation);
        NormalizeNavigation(cfg.StuckRecoveryNavigation);
        NormalizeNavigation(cfg.DeathReturnNavigation);
        cfg.Points ??= new List<ReturnPoint>();

        foreach (var point in cfg.Points)
        {
            point.MapName = point.MapName.Trim();
            point.Label = point.Label.Trim();
            point.SourceModule = point.SourceModule.Trim();
            point.SourcePath = point.SourcePath.Trim();
        }
    }

    private static void NormalizeNavigation(ReturnNavigationConfig nav)
    {
        nav.RecordedPreHotkeys ??= new List<RecordedNavigationHotkey>();
        nav.RecordedClicks ??= new List<RecordedNavigationClick>();
    }

    private static bool IsLegacyDefaultMapNameRegion(OcrRegionConfig region) =>
        region.X == 2365
        && region.Y == 15
        && (region.Width == 105 || region.Width == 150)
        && region.Height == 20;

    private void ReconcileConfirmedMapNameFromConfiguredMaps(bool allowAutoFillWhenBlank)
    {
        var recognition = Current.NavigationRecognition;
        var confirmed = NormalizeMapName(recognition.ConfirmedMapName);
        if (!string.Equals(recognition.ConfirmedMapName, confirmed, StringComparison.Ordinal))
            recognition.ConfirmedMapName = confirmed;

        var maps = Current.Points
            .Select(p => NormalizeMapName(p.MapName))
            .Where(IsUsableMapName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (recognition.ConfirmedMapNameAutoFilled)
        {
            if (maps.Length == 1)
            {
                recognition.ConfirmedMapName = maps[0];
                return;
            }

            recognition.ConfirmedMapName = "";
            recognition.ConfirmedMapNameAutoFilled = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(confirmed))
        {
            if (maps.Length > 0 && !maps.Contains(confirmed, StringComparer.Ordinal))
            {
                recognition.ConfirmedMapName = "";
                _log.Warn(Module, $"已清空失效的当前地图确认：{confirmed}");
            }

            return;
        }

        if (!allowAutoFillWhenBlank)
            return;

        if (maps.Length != 1)
            return;

        recognition.ConfirmedMapName = maps[0];
        recognition.ConfirmedMapNameAutoFilled = true;
        _log.Info(Module, $"已根据单地图返点配置自动确认地图：{maps[0]}。");
    }

    private static string NormalizeMapName(string mapName)
    {
        var chars = mapName.Trim()
            .Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c))
            .ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static bool IsUsableMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        if (mapName.Contains("未知", StringComparison.Ordinal))
            return false;

        var meaningful = mapName.Count(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsLetterOrDigit(c));
        return meaningful >= 2;
    }
}
