using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class LifeCollectConfigService : ILifeCollectConfigService
{
    private const string Module = "LifeCollectConfig";
    private const string ProfilePrefix = "life-collect-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static LifeCollectConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public LifeCollectConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public LifeCollectConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<string> MapNames =>
        Current.Points
            .Select(p => p.MapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<LifeCollectPoint> CurrentRoute =>
        Current.Points
            .Where(p => string.Equals(p.MapName, Current.MapName, StringComparison.Ordinal))
            .OrderBy(p => p.Label, StringComparer.Ordinal)
            .ToList();

    public LifeCollectImportResult ImportLegacyDirectory(string directory, string sourceModule)
    {
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

        var mapCount = 0;
        var pointCount = 0;
        var skipped = 0;

        foreach (var file in Directory.EnumerateFiles(full, "*.txt", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var mapName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(mapName) || mapName.Contains("注意", StringComparison.Ordinal))
                continue;

            var addedForMap = 0;
            foreach (var raw in ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                var xy = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
                {
                    skipped++;
                    continue;
                }

                Current.Points.Add(new LifeCollectPoint
                {
                    MapName = mapName,
                    X = x,
                    Y = y,
                    Label = $"{mapName}-{addedForMap + 1:D2}",
                    SourceModule = sourceModule,
                    SourcePath = ""
                });
                addedForMap++;
                pointCount++;
            }

            if (addedForMap > 0)
                mapCount++;
        }

        if (Current.Points.Count > 0 && string.IsNullOrWhiteSpace(Current.MapName))
            Current.MapName = Current.Points[0].MapName;

        var result = new LifeCollectImportResult(full, mapCount, pointCount, skipped);
        _log.Info(Module, $"路线已载入：地图 {mapCount} | 坐标 {pointCount} | 跳过 {skipped}");
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void AddPoint(LifeCollectPoint point)
    {
        if (string.IsNullOrWhiteSpace(point.MapName))
            throw new ArgumentException("Map name cannot be empty.", nameof(point));
        if (point.X < 0 || point.Y < 0)
            throw new ArgumentException("Coordinates cannot be negative.", nameof(point));

        Current.Points.Add(point);
        _log.Info(Module, $"已添加生活采集坐标：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePoint(string id)
    {
        var point = Current.Points.FirstOrDefault(p => p.Id == id);
        if (point is null)
            return false;

        Current.Points.Remove(point);
        _log.Info(Module, $"已删除生活采集坐标：{point.MapName} {point.X},{point.Y}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        Current.Points.Clear();
        _log.Info(Module, "已清空生活采集坐标。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"生活采集配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<LifeCollectConfig>(json);
            if (cfg is null)
                return false;

            Current = cfg;
            _log.Info(Module, $"生活采集配置已加载：{profileName}");
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
}
