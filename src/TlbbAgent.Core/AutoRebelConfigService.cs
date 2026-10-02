using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class AutoRebelConfigService : IAutoRebelConfigService
{
    private const string Module = "AutoRebelConfig";
    private const string ProfilePrefix = "auto-rebel-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static AutoRebelConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public AutoRebelConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public AutoRebelConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<string> MapNames =>
        Current.RoutePoints
            .Select(p => p.MapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    public void AddPoint(AutoRebelRoutePoint point)
    {
        if (string.IsNullOrWhiteSpace(point.MapName))
            throw new ArgumentException("Map name cannot be empty.", nameof(point));
        if (point.X < 0 || point.Y < 0)
            throw new ArgumentException("Coordinates cannot be negative.", nameof(point));

        Current.RoutePoints.Add(point);
        _log.Info(Module, $"已添加自动反贼路线点：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePoint(string id)
    {
        var point = Current.RoutePoints.FirstOrDefault(p => p.Id == id);
        if (point is null)
            return false;

        Current.RoutePoints.Remove(point);
        _log.Info(Module, $"已删除自动反贼路线点：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        Current.RoutePoints.Clear();
        _log.Info(Module, "已清空自动反贼路线点。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public AutoRebelImportResult ImportLegacyRoutes(string path, string sourceModule)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Route file does not exist.", fullPath);

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

                Current.RoutePoints.Add(new AutoRebelRoutePoint
                {
                    MapName = mapName,
                    X = x,
                    Y = y,
                    Label = $"{mapName}-{addedForMap + 1}",
                    SourceModule = sourceModule,
                    SourcePath = ""
                });
                addedForMap++;
                pointCount++;
            }

            if (addedForMap > 0)
                mapCount++;
        }

        Current.SourceSummary = "路线已载入。";
        var result = new AutoRebelImportResult(fullPath, sourceModule, mapCount, pointCount, skipped);
        _log.Info(Module, $"路线已载入：地图 {mapCount} | 坐标 {pointCount} | 跳过 {skipped}");
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"自动反贼配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<AutoRebelConfig>(json);
            if (cfg is null)
                return false;

            Current = cfg;
            _log.Info(Module, $"自动反贼配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static string AppendSource(string current, string source) =>
        string.IsNullOrWhiteSpace(current) ? source : current + "\n" + source;

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
}

