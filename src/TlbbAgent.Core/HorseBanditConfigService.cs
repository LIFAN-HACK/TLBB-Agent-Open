using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class HorseBanditConfigService : IHorseBanditConfigService
{
    private const string Module = "HorseBanditConfig";
    private const string ProfilePrefix = "horse-bandit-";
    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    static HorseBanditConfigService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public HorseBanditConfigService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public HorseBanditConfig Current { get; private set; } = new();
    public event EventHandler? Changed;

    public IReadOnlyList<string> MapNames =>
        Current.RoutePoints
            .Select(p => p.MapName)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    public HorseBanditImportResult ImportLegacyConfig(string path, string sourceModule)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Config file does not exist.", fullPath);

        var values = ReadIni(fullPath);
        var fieldCount = 0;

        fieldCount += ApplyBool(values, "界面", "双开", v => Current.Role = v ? HorseBanditRole.Member : Current.Role);
        fieldCount += ApplyInt(values, "界面", "找马", v => Current.FindHorseMode = v);
        fieldCount += ApplyBool(values, "界面", "同意进队", v => Current.AgreeTeamInvite = v);
        fieldCount += ApplyBool(values, "界面", "同意邀请", v => Current.AgreeTeamInvite = v);
        fieldCount += ApplyBool(values, "界面", "等待队员", v => Current.WaitMembers = v);
        fieldCount += ApplyInt(values, "界面", "等待队员时间", v => Current.WaitMemberSeconds = v);
        fieldCount += ApplyBool(values, "界面", "队员选怪打马", v => Current.MemberSelectsTarget = v);
        fieldCount += ApplyBool(values, "界面", "队长不打怪", v => Current.LeaderNoAttack = v);
        fieldCount += ApplyBool(values, "界面", "队长不下马", v => Current.LeaderStayMounted = v);
        fieldCount += ApplyBool(values, "界面", "队员打所有怪", v => Current.MemberAttackAllTargets = v);
        fieldCount += ApplyBool(values, "界面", "打所有怪", v => Current.MemberAttackAllTargets = v);
        fieldCount += ApplyBool(values, "界面", "开马前不检测队员", v => Current.CheckMembersBeforeStart = !v);
        fieldCount += ApplyBool(values, "界面", "开马后不检测队员", v => Current.CheckMembersAfterStart = !v);
        fieldCount += ApplyBool(values, "界面", "巡逻不检测队员", v => Current.CheckMembersDuringPatrol = !v);
        fieldCount += ApplyInt(values, "界面", "转圈拉跟随次数", v => Current.CirclePullFollowCount = v);
        fieldCount += ApplyInt(values, "界面", "掉队距离", v => Current.LostFollowDistance = v);
        fieldCount += ApplyInt(values, "界面", "向前距离", v => Current.ForwardDistance = v);
        fieldCount += ApplyInt(values, "界面", "提前距离", v => Current.LeadDistance = v);
        fieldCount += ApplyInt(values, "界面", "等待x", v => Current.WaitX = v);
        fieldCount += ApplyInt(values, "界面", "等待y", v => Current.WaitY = v);
        fieldCount += ApplyBool(values, "界面", "内置坐标", v => Current.UseBuiltInRoutes = v);
        fieldCount += ApplyBool(values, "界面", "自定义坐标", v => Current.UseCustomRoutes = v);
        fieldCount += ApplyString(values, "界面", "不去的地图", v => Current.AvoidMaps = v);
        fieldCount += ApplyHotkey(values, "界面", "普攻快键", v =>
        {
            Current.SearchHotkey = v;
            Current.AttackHotkey = v;
        });
        fieldCount += ApplyHotkey(values, "界面", "坐骑快键", v => Current.MountHotkey = v);

        Current.SourceSummary = "配置已载入。";
        var result = new HorseBanditImportResult(fullPath, sourceModule, fieldCount, 0, 0, 0);
        _log.Info(Module, $"配置已载入：字段 {fieldCount}");
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public HorseBanditImportResult ImportLegacyRoutes(string path, string sourceModule)
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

                Current.RoutePoints.Add(new HorseBanditRoutePoint
                {
                    MapName = mapName,
                    X = x,
                    Y = y,
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
        var result = new HorseBanditImportResult(fullPath, sourceModule, 0, mapCount, pointCount, skipped);
        _log.Info(Module, $"路线已载入：地图 {mapCount} | 坐标 {pointCount} | 跳过 {skipped}");
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Save(string profileName)
    {
        Current.ProfileName = profileName;
        _profiles.Save(ProfilePrefix + profileName, JsonSerializer.Serialize(Current, JsonOptions));
        _log.Info(Module, $"马贼配置已保存：{profileName}");
    }

    public bool Load(string profileName)
    {
        var json = _profiles.Load(ProfilePrefix + profileName);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<HorseBanditConfig>(json);
            if (cfg is null)
                return false;

            Current = cfg;
            _log.Info(Module, $"马贼配置已加载：{profileName}");
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch
        {
            _log.Error(Module, "配置加载失败。");
            return false;
        }
    }

    private static int ApplyBool(Dictionary<string, string> values, string section, string key, Action<bool> apply)
    {
        if (!TryGet(values, section, key, out var value))
            return 0;

        apply(ParseBool(value));
        return 1;
    }

    private static int ApplyInt(Dictionary<string, string> values, string section, string key, Action<int> apply)
    {
        if (!TryGet(values, section, key, out var value) || !int.TryParse(value, out var number))
            return 0;

        apply(number);
        return 1;
    }

    private static int ApplyString(Dictionary<string, string> values, string section, string key, Action<string> apply)
    {
        if (!TryGet(values, section, key, out var value))
            return 0;

        apply(value);
        return 1;
    }

    private static int ApplyHotkey(Dictionary<string, string> values, string section, string key, Action<string> apply)
    {
        if (!TryGet(values, section, key, out var value))
            return 0;

        var hotkey = value.Trim();
        if (hotkey == "-1")
            hotkey = string.Empty;
        apply(hotkey);
        return 1;
    }

    private static bool TryGet(Dictionary<string, string> values, string section, string key, out string value) =>
        values.TryGetValue(section + "." + key, out value!);

    private static bool ParseBool(string value) =>
        value.Trim() is "真" or "true" or "True" or "1" or "是";

    private static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var split = line.Split('=', 2);
            if (split.Length != 2)
                continue;

            result[section + "." + split[0].Trim()] = split[1].Trim();
        }

        return result;
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
