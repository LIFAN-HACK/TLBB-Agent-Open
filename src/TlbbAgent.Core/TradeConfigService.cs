using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class TradeConfigService : ITradeConfigService
{
    private const string Module = "TradeConfig";
    private const string ProfilePrefix = "trade-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static TradeConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TradeConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public TradeConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<string> MapNames =>
        Current.Points
            .Select(p => p.MapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<TradeRoutePoint> Route =>
        Current.Points
            .OrderBy(p => p.Label, StringComparer.Ordinal)
            .ToList();

    public TradeImportResult ImportPlainRoute(string path, string sourceModule)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("Trade route file does not exist.", full);

        var added = 0;
        var skipped = 0;
        foreach (var raw in ReadAllLines(full))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var point = ParseLine(line, sourceModule, full, Current.Points.Count + added + 1);
            if (point is null)
            {
                skipped++;
                continue;
            }

            Current.Points.Add(point);
            added++;
        }

        Current.SourceSummary = $"路线已载入：{added} 步。";
        var message = $"路线已载入：步骤 {added}，跳过 {skipped}。";
        _log.Info(Module, message);
        Changed?.Invoke(this, EventArgs.Empty);
        return new TradeImportResult(full, added, skipped, message);
    }

    public void AddPoint(TradeRoutePoint point)
    {
        if (string.IsNullOrWhiteSpace(point.MapName))
            throw new ArgumentException("Map name cannot be empty.", nameof(point));
        if (point.X < 0 || point.Y < 0)
            throw new ArgumentException("Coordinates cannot be negative.", nameof(point));

        if (string.IsNullOrWhiteSpace(point.Label))
            point.Label = $"{Current.Points.Count + 1:D2}-{point.Action}";

        Current.Points.Add(point);
        _log.Info(Module, $"已添加跑商步骤：{point.MapName} {point.X},{point.Y} [{point.Action}]");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemovePoint(string id)
    {
        var point = Current.Points.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (point is null)
            return false;

        Current.Points.Remove(point);
        _log.Info(Module, $"已删除跑商步骤：{point.Label}");
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        Current.Points.Clear();
        _log.Info(Module, "已清空跑商路线。");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"跑商配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<TradeConfig>(json);
            if (cfg is null)
                return false;

            Current = cfg;
            _log.Info(Module, $"跑商配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static TradeRoutePoint? ParseLine(string line, string sourceModule, string sourcePath, int index)
    {
        var parts = SplitFields(line);
        if (parts.Count < 3)
            return null;

        var map = parts[0];
        if (!int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
            return null;

        var action = parts.Count > 3 && Enum.TryParse<TradeRouteAction>(NormalizeAction(parts[3]), ignoreCase: true, out var parsed)
            ? parsed
            : TradeRouteAction.Navigate;

        return new TradeRoutePoint
        {
            MapName = map,
            X = x,
            Y = y,
            Action = action,
            NpcName = parts.Count > 4 ? parts[4] : "",
            GoodsName = parts.Count > 5 ? parts[5] : "",
            Hotkey = parts.Count > 6 ? parts[6] : "",
            ClickX = parts.Count > 7 && int.TryParse(parts[7], out var clickX) ? clickX : 520,
            ClickY = parts.Count > 8 && int.TryParse(parts[8], out var clickY) ? clickY : 360,
            WaitMs = parts.Count > 9 && int.TryParse(parts[9], out var wait) ? wait : 900,
            Label = $"{index:D2}-{action}",
            SourceModule = sourceModule,
            SourcePath = ""
        };
    }

    private static string NormalizeAction(string value) =>
        value.Trim() switch
        {
            "买" or "买入" => nameof(TradeRouteAction.Buy),
            "卖" or "卖出" => nameof(TradeRouteAction.Sell),
            "对话" or "NPC" => nameof(TradeRouteAction.TalkNpc),
            "热键" or "按键" => nameof(TradeRouteAction.Hotkey),
            "点击" => nameof(TradeRouteAction.Click),
            _ => value.Trim()
        };

    private static List<string> SplitFields(string line)
    {
        var delimiter = line.Contains('\t') ? '\t' : line.Contains('|') ? '|' : ',';
        return line.Split(delimiter, StringSplitOptions.TrimEntries).ToList();
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
}
