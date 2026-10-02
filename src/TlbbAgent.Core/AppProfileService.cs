using System.Text.Json;
using System.Text.RegularExpressions;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class AppProfileService : IAppProfileService
{
    private const string StateProfileName = "_app-state";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] InternalPrefixes =
    {
        "_",
        "diagnostic-",
        "return-points-",
        "horse-bandit-",
        "npc-grab-",
        "auto-rebel-",
        "auto-monk-",
        "auto-reconnect-",
        "auto-login-",
        "dungeon-",
        "life-collect-",
        "trade-",
        "material-craft-",
        "school-task-",
        "treasure-map-",
        "guild-contribution-",
        "utility-",
        "shout-",
        "resolution-profiles"
    };

    private static readonly string[] HiddenProfileNameFragments =
    {
        "diagnostic",
        "synthetic",
        "smoke",
        "dryrun",
        "execute",
        "inverty",
        "repeat",
        "test",
        "诊断",
        "测试",
        "证据",
        "预演",
        "回归"
    };

    private readonly IProfileService _profiles;
    private readonly IGlobalConfigService _global;
    private readonly IReturnPointService _returnPoints;
    private readonly IHorseBanditConfigService _horseBandit;
    private readonly INpcGrabConfigService _npcGrab;
    private readonly IAutoRebelConfigService _autoRebel;
    private readonly IAutoMonkConfigService _autoMonk;
    private readonly IAutoReconnectConfigService _autoReconnect;
    private readonly IAutoLoginConfigService _autoLogin;
    private readonly IDungeonConfigService _dungeon;
    private readonly ILifeCollectConfigService _lifeCollect;
    private readonly ITradeConfigService _trade;
    private readonly IMaterialCraftConfigService _materialCraft;
    private readonly ISchoolTaskConfigService _schoolTask;
    private readonly ITreasureMapConfigService _treasureMap;
    private readonly IGuildContributionConfigService _guildContribution;
    private readonly IUtilityConfigService _utility;
    private readonly IShoutConfigService _shout;
    private readonly ILogService _log;
    private readonly Dictionary<string, string> _sessionWindowProfileMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppProfileState _state;
    private string? _currentProfileName;

    public AppProfileService(
        IProfileService profiles,
        IGlobalConfigService global,
        IReturnPointService returnPoints,
        IHorseBanditConfigService horseBandit,
        INpcGrabConfigService npcGrab,
        IAutoRebelConfigService autoRebel,
        IAutoMonkConfigService autoMonk,
        IAutoReconnectConfigService autoReconnect,
        IAutoLoginConfigService autoLogin,
        IDungeonConfigService dungeon,
        ILifeCollectConfigService lifeCollect,
        ITradeConfigService trade,
        IMaterialCraftConfigService materialCraft,
        ISchoolTaskConfigService schoolTask,
        ITreasureMapConfigService treasureMap,
        IGuildContributionConfigService guildContribution,
        IUtilityConfigService utility,
        IShoutConfigService shout,
        ILogService log)
    {
        _profiles = profiles;
        _global = global;
        _returnPoints = returnPoints;
        _horseBandit = horseBandit;
        _npcGrab = npcGrab;
        _autoRebel = autoRebel;
        _autoMonk = autoMonk;
        _autoReconnect = autoReconnect;
        _autoLogin = autoLogin;
        _dungeon = dungeon;
        _lifeCollect = lifeCollect;
        _trade = trade;
        _materialCraft = materialCraft;
        _schoolTask = schoolTask;
        _treasureMap = treasureMap;
        _guildContribution = guildContribution;
        _utility = utility;
        _shout = shout;
        _log = log;
        _state = LoadState();
        _currentProfileName = IsUserProfileName(_state.LastProfileName) ? _state.LastProfileName : null;
    }

    public string? CurrentProfileName => _currentProfileName;

    public IReadOnlyList<string> ListProfileNames() =>
        _profiles.ListProfileNames()
            .Where(IsUserProfileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public void SaveAll(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            profileName = $"方案-{DateTime.Now:MMdd-HHmm}";

        using (_log.Suppress(entry => entry.Level == LogLevel.Info))
        {
        _global.Save(profileName);
        _returnPoints.Save(profileName);
        _horseBandit.Save(profileName);
        _npcGrab.Save(profileName);
        _autoRebel.Save(profileName);
        _autoMonk.Save(profileName);
        _autoReconnect.Save(profileName);
        _autoLogin.Save(profileName);
        _dungeon.Save(profileName);
        _lifeCollect.Save(profileName);
        _trade.Save(profileName);
        _materialCraft.Save(profileName);
        _schoolTask.Save(profileName);
        _treasureMap.Save(profileName);
        _guildContribution.Save(profileName);
        _utility.Save(profileName);
        _shout.Save(profileName);
        SetCurrentProfile(profileName);
        }
        _log.Info("Profile", $"完整方案已保存：{profileName}");
    }

    public void RenameAll(string oldProfileName, string newProfileName)
    {
        if (string.IsNullOrWhiteSpace(newProfileName))
            return;

        if (string.Equals(oldProfileName, newProfileName, StringComparison.Ordinal))
        {
            SaveAll(newProfileName);
            return;
        }

        SaveAll(newProfileName);
        if (!string.IsNullOrWhiteSpace(oldProfileName))
        {
            _profiles.Delete(oldProfileName);
            foreach (var prefix in InternalPrefixes.Where(prefix => prefix.EndsWith("-", StringComparison.Ordinal)))
                _profiles.Delete(prefix + oldProfileName);
        }

        foreach (var key in _state.WindowProfileMap.Keys.ToList())
        {
            if (string.Equals(_state.WindowProfileMap[key], oldProfileName, StringComparison.Ordinal))
                _state.WindowProfileMap[key] = newProfileName;
        }
        foreach (var key in _sessionWindowProfileMap.Keys.ToList())
        {
            if (string.Equals(_sessionWindowProfileMap[key], oldProfileName, StringComparison.Ordinal))
                _sessionWindowProfileMap[key] = newProfileName;
        }

        SetCurrentProfile(newProfileName);
        _log.Info("Profile", $"方案已更名：{oldProfileName} -> {newProfileName}");
    }

    public AppProfileLoadResult LoadAll(string profileName)
    {
        var loaded = new List<string>();
        var missing = new List<string>();

        var globalLoaded = _global.Load(profileName);
        Track(globalLoaded, "通用配置", loaded, missing);
        Track(_returnPoints.Load(profileName), "返点配置", loaded, missing);
        Track(_horseBandit.Load(profileName), "马贼配置", loaded, missing);
        Track(_npcGrab.Load(profileName), "抢NPC配置", loaded, missing);
        Track(_autoRebel.Load(profileName), "反贼配置", loaded, missing);
        Track(_autoMonk.Load(profileName), "和尚配置", loaded, missing);
        Track(_autoReconnect.Load(profileName), "自动重连配置", loaded, missing);
        Track(_autoLogin.Load(profileName), "自动上号配置", loaded, missing);
        Track(_dungeon.Load(profileName), "三环配置", loaded, missing);
        Track(_lifeCollect.Load(profileName), "生活采集配置", loaded, missing);
        Track(_trade.Load(profileName), "跑商配置", loaded, missing);
        Track(_materialCraft.Load(profileName), "材料加工配置", loaded, missing);
        Track(_schoolTask.Load(profileName), "单师门配置", loaded, missing);
        Track(_treasureMap.Load(profileName), "挖图配置", loaded, missing);
        Track(_guildContribution.Load(profileName), "帮贡配置", loaded, missing);
        Track(_utility.Load(profileName), "便捷配置", loaded, missing);
        Track(_shout.Load(profileName), "喊话配置", loaded, missing);

        SetCurrentProfile(profileName);
        if (missing.Count == 0)
            _log.Info("Profile", $"完整方案已加载：{profileName}");
        else
            _log.Warn("Profile", $"方案已部分加载：{profileName}；缺少 {string.Join("、", missing)}");

        return new AppProfileLoadResult(profileName, globalLoaded, loaded, missing);
    }

    public string? ResolveProfileForWindow(BoundGameWindow window)
    {
        var sessionKey = BuildSessionWindowKey(window);
        if (!string.IsNullOrWhiteSpace(sessionKey)
            && _sessionWindowProfileMap.TryGetValue(sessionKey, out var sessionProfileName)
            && IsUserProfileName(sessionProfileName)
            && ListProfileNames().Contains(sessionProfileName, StringComparer.Ordinal))
        {
            return sessionProfileName;
        }

        var key = BuildWindowKey(window);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        if (_state.WindowProfileMap.TryGetValue(key, out var profileName)
            && IsUserProfileName(profileName)
            && ListProfileNames().Contains(profileName, StringComparer.Ordinal))
        {
            RememberSessionWindowProfile(sessionKey, profileName);
            return profileName;
        }

        var legacyKey = BuildLegacyWindowKey(window);
        if (!string.Equals(legacyKey, key, StringComparison.OrdinalIgnoreCase)
            && _state.WindowProfileMap.TryGetValue(legacyKey, out profileName)
            && IsUserProfileName(profileName)
            && ListProfileNames().Contains(profileName, StringComparer.Ordinal))
        {
            _state.WindowProfileMap[key] = profileName;
            RememberSessionWindowProfile(sessionKey, profileName);
            PersistState();
            return profileName;
        }

        foreach (var (storedKey, storedProfileName) in _state.WindowProfileMap)
        {
            if (!string.Equals(NormalizeStoredWindowKey(storedKey), key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsUserProfileName(storedProfileName)
                || !ListProfileNames().Contains(storedProfileName, StringComparer.Ordinal))
                continue;

            _state.WindowProfileMap[key] = storedProfileName;
            RememberSessionWindowProfile(sessionKey, storedProfileName);
            PersistState();
            return storedProfileName;
        }

        return null;
    }

    public void RememberWindowProfile(BoundGameWindow window, string profileName)
    {
        if (!IsUserProfileName(profileName))
            return;

        var key = BuildWindowKey(window);
        if (string.IsNullOrWhiteSpace(key))
            return;

        RememberSessionWindowProfile(BuildSessionWindowKey(window), profileName);
        _state.WindowProfileMap[key] = profileName;
        PersistState();
    }

    private void RememberSessionWindowProfile(string sessionKey, string profileName)
    {
        if (!string.IsNullOrWhiteSpace(sessionKey))
            _sessionWindowProfileMap[sessionKey] = profileName;
    }

    private static void Track(bool success, string name, List<string> loaded, List<string> missing)
    {
        if (success) loaded.Add(name);
        else missing.Add(name);
    }

    private void SetCurrentProfile(string profileName)
    {
        _currentProfileName = profileName;
        _state.LastProfileName = profileName;
        PersistState();
    }

    private static bool IsUserProfileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var trimmed = name.Trim();
        return !InternalPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               && !HiddenProfileNameFragments.Any(fragment => trimmed.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildWindowKey(BoundGameWindow window)
    {
        var path = (window.ProcessPath ?? string.Empty).Trim().ToLowerInvariant();
        var title = NormalizeWindowTitle(window.Title ?? string.Empty);
        return string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : $"{path}|{title}";
    }

    private static string BuildSessionWindowKey(BoundGameWindow window) =>
        window.Hwnd == nint.Zero
            ? string.Empty
            : $"{window.Pid}:{window.Hwnd.ToInt64():X}";

    private static string BuildLegacyWindowKey(BoundGameWindow window)
    {
        var path = (window.ProcessPath ?? string.Empty).Trim().ToLowerInvariant();
        var title = (window.Title ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : $"{path}|{title}";
    }

    private static string NormalizeWindowTitle(string title)
    {
        title = title.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = Regex.Replace(title, @"\s+\d+(?:\.\d+){1,4}(?=\s|\(|（|$)", "", RegexOptions.CultureInvariant);
        title = Regex.Replace(title, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return title;
    }

    private static string NormalizeStoredWindowKey(string storedKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey))
            return string.Empty;

        var separator = storedKey.IndexOf('|');
        if (separator < 0)
            return storedKey.Trim().ToLowerInvariant();

        var path = storedKey[..separator].Trim().ToLowerInvariant();
        var title = NormalizeWindowTitle(storedKey[(separator + 1)..]);
        return string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : $"{path}|{title}";
    }

    private AppProfileState LoadState()
    {
        try
        {
            if (!_profiles.ListProfileNames().Contains(StateProfileName, StringComparer.Ordinal))
                return new AppProfileState();

            var json = _profiles.Load(StateProfileName);
            return string.IsNullOrWhiteSpace(json)
                ? new AppProfileState()
                : JsonSerializer.Deserialize<AppProfileState>(json) ?? new AppProfileState();
        }
        catch
        {
            return new AppProfileState();
        }
    }

    private void PersistState()
    {
        try
        {
            _profiles.Save(StateProfileName, JsonSerializer.Serialize(_state, JsonOptions));
        }
        catch
        {
            _log.Warn("Profile", "当前方案状态保存失败。");
        }
    }

    private sealed class AppProfileState
    {
        public string? LastProfileName { get; set; }
        public Dictionary<string, string> WindowProfileMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
