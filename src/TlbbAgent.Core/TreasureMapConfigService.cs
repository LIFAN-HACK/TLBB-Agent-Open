using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class TreasureMapConfigService : ITreasureMapConfigService
{
    private const string Module = "TreasureMapConfig";
    private const string ProfilePrefix = "treasure-map-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static TreasureMapConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TreasureMapConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public TreasureMapConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<TreasureMapPoint> Route => Current.Points.ToList();
    public IReadOnlyList<string> KeepItems => Current.KeepItems.ToList();

    public TreasureMapImportResult ImportCoordinateFile(string path, string sourceModule, string mapName)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Coordinate file does not exist.", full);

        var parsed = new List<TreasureMapPoint>();
        var skipped = 0;
        foreach (var raw in ReadAllLines(full))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var xy = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
            {
                skipped++;
                continue;
            }

            parsed.Add(new TreasureMapPoint
            {
                MapName = string.IsNullOrWhiteSpace(mapName) ? Current.MapName : mapName.Trim(),
                X = x,
                Y = y,
                Label = $"{parsed.Count + 1:D2}-挖图",
                Action = TreasureMapStepAction.Dig,
                Hotkey = Current.DigHotkey,
                ClickX = 520,
                ClickY = 360,
                WaitMs = Current.ActionIntervalMs,
                SourceModule = sourceModule,
                SourcePath = ""
            });
        }

        if (parsed.Count > 0)
        {
            Current.Points = parsed;
            Current.MapName = parsed[0].MapName;
        }

        Current.SourceSummary = $"坐标已载入：{parsed.Count} 个。";
        var message = $"坐标已载入：{parsed.Count} 个，跳过 {skipped}。";
        _log.Info(Module, message);
        Changed?.Invoke(this, EventArgs.Empty);
        return new TreasureMapImportResult(full, parsed.Count, Current.KeepItems.Count, skipped, message);
    }

    public int LoadKeepItems(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Item list file does not exist.", full);

        var items = ReadAllLines(full)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            .Distinct(StringComparer.Ordinal)
            .Take(1000)
            .ToList();

        Current.KeepItems = items;
        _log.Info(Module, $"保留物品已载入：{items.Count}。");
        Changed?.Invoke(this, EventArgs.Empty);
        return items.Count;
    }

    public void AddPoint(TreasureMapPoint point)
    {
        NormalizePoint(point, Current);
        if (string.IsNullOrWhiteSpace(point.Label))
            point.Label = $"{Current.Points.Count + 1:D2}-{point.Action}";

        Current.Points.Add(point);
        _log.Info(Module, $"已添加挖图坐标：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePoint(string id)
    {
        var point = Current.Points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (point is null)
            return false;

        Current.Points.Remove(point);
        _log.Info(Module, $"已删除挖图坐标：{point.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ClearPoints()
    {
        Current.Points.Clear();
        _log.Info(Module, "已清空挖图坐标。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        Normalize(Current);
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"挖图配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<TreasureMapConfig>(json);
            if (cfg is null)
                return false;

            Normalize(cfg);
            Current = cfg;
            _log.Info(Module, $"挖图配置已加载：{profileName}");
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
            return SplitLines(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void Normalize(TreasureMapConfig cfg)
    {
        cfg.MapBatchCount = Math.Clamp(cfg.MapBatchCount, 1, 999);
        cfg.ArrivalDistance = Math.Clamp(cfg.ArrivalDistance, 1, 100);
        cfg.StepIntervalMs = Math.Clamp(cfg.StepIntervalMs, 50, 60000);
        cfg.ActionIntervalMs = Math.Clamp(cfg.ActionIntervalMs, 50, 60000);
        cfg.MaxPointRetry = Math.Clamp(cfg.MaxPointRetry, 0, 20);
        cfg.Points ??= new List<TreasureMapPoint>();
        cfg.KeepItems ??= new List<string>();
        foreach (var point in cfg.Points)
            NormalizePoint(point, cfg);
    }

    private static void NormalizePoint(TreasureMapPoint point, TreasureMapConfig cfg)
    {
        point.MapName = string.IsNullOrWhiteSpace(point.MapName) ? cfg.MapName : point.MapName.Trim();
        point.X = Math.Max(0, point.X);
        point.Y = Math.Max(0, point.Y);
        point.ClickX = Math.Max(0, point.ClickX);
        point.ClickY = Math.Max(0, point.ClickY);
        point.WaitMs = Math.Clamp(point.WaitMs, 0, 60000);
        point.RetryLimit = Math.Clamp(point.RetryLimit, 0, 20);
        point.SourcePath = "";
        if (string.IsNullOrWhiteSpace(point.SourceModule))
            point.SourceModule = "配置";
    }
}
