using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class ResolutionProfileService : IResolutionProfileService
{
    private const string StoreName = "resolution-profiles";
    private const string Module = "ResolutionProfile";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IProfileService _profiles;
    private readonly ILogService _log;
    private ResolutionProfileStore _store = new();
    private bool _loaded;

    public ResolutionProfileService(IProfileService profiles, ILogService log)
    {
        _profiles = profiles;
        _log = log;
    }

    public IReadOnlyList<ResolutionCombatProfile> CombatProfiles
    {
        get
        {
            EnsureLoaded();
            return _store.CombatProfiles
                .OrderBy(p => p.ClientWidth)
                .ThenBy(p => p.ClientHeight)
                .ToList();
        }
    }

    public CombatRecognitionConfig ResolveCombat(int clientWidth, int clientHeight, CombatRecognitionConfig fallback)
    {
        EnsureLoaded();
        return TryGetCombatProfile(clientWidth, clientHeight, out var profile)
            ? CloneCombat(profile.CombatRecognition)
            : fallback;
    }

    public OcrRegionConfig ResolveAvatar(int clientWidth, int clientHeight, OcrRegionConfig fallback)
    {
        EnsureLoaded();
        return TryGetCombatProfile(clientWidth, clientHeight, out var profile)
            ? CloneRegion(profile.AvatarRegion)
            : fallback;
    }

    public void SaveCombatProfile(int clientWidth, int clientHeight, CombatRecognitionConfig config, string source)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientWidth), "Client size must be positive.");

        EnsureLoaded();
        var existing = GetOrCreateProfile(clientWidth, clientHeight);

        existing.CombatRecognition = CloneCombat(config);
        existing.Source = source;
        existing.UpdatedAt = DateTimeOffset.Now;
        SaveStore();
        _log.Info(Module, $"识别方案已保存：{clientWidth}x{clientHeight}");
    }

    public void SaveAvatarProfile(int clientWidth, int clientHeight, OcrRegionConfig avatarRegion, string source)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientWidth), "Client size must be positive.");

        EnsureLoaded();
        var existing = GetOrCreateProfile(clientWidth, clientHeight);
        existing.AvatarRegion = CloneRegion(avatarRegion);
        existing.AvatarSource = source;
        existing.UpdatedAt = DateTimeOffset.Now;
        SaveStore();
        _log.Info(Module, $"Saved avatar profile: {clientWidth}x{clientHeight} | {source}");
    }

    public bool TryGetCombatProfile(int clientWidth, int clientHeight, out ResolutionCombatProfile profile)
    {
        EnsureLoaded();
        var found = _store.CombatProfiles.FirstOrDefault(p => p.ClientWidth == clientWidth && p.ClientHeight == clientHeight);
        if (found is null)
        {
            profile = new ResolutionCombatProfile();
            return false;
        }

        profile = CloneProfile(found);
        return true;
    }

    public bool DeleteCombatProfile(int clientWidth, int clientHeight)
    {
        EnsureLoaded();
        var removed = _store.CombatProfiles.RemoveAll(p => p.ClientWidth == clientWidth && p.ClientHeight == clientHeight);
        if (removed <= 0)
            return false;

        SaveStore();
        _log.Info(Module, $"识别方案已删除：{clientWidth}x{clientHeight}");
        return true;
    }

    public void Reload()
    {
        _loaded = false;
        _store = new ResolutionProfileStore();
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!_profiles.ListProfileNames().Contains(StoreName, StringComparer.Ordinal))
            return;

        var json = _profiles.Load(StoreName);
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            _store = JsonSerializer.Deserialize<ResolutionProfileStore>(json) ?? new ResolutionProfileStore();
            NormalizeStore();
            _log.Info(Module, "校准配置已加载。");
        }
        catch
        {
            _store = new ResolutionProfileStore();
            _log.Error(Module, "校准配置加载失败。");
        }
    }

    private void SaveStore()
    {
        NormalizeStore();
        _profiles.Save(StoreName, JsonSerializer.Serialize(_store, JsonOptions));
    }

    private void NormalizeStore()
    {
        _store.CombatProfiles = _store.CombatProfiles
            .Where(p => p.ClientWidth > 0 && p.ClientHeight > 0)
            .GroupBy(p => (p.ClientWidth, p.ClientHeight))
            .Select(g => g.OrderByDescending(p => p.UpdatedAt).First())
            .OrderBy(p => p.ClientWidth)
            .ThenBy(p => p.ClientHeight)
            .ToList();

        foreach (var profile in _store.CombatProfiles)
        {
            profile.CombatRecognition ??= new CombatRecognitionConfig();
            profile.AvatarRegion ??= CreateDefaultAvatarRegion();
        }
    }

    private ResolutionCombatProfile GetOrCreateProfile(int clientWidth, int clientHeight)
    {
        var existing = _store.CombatProfiles.FirstOrDefault(p => p.ClientWidth == clientWidth && p.ClientHeight == clientHeight);
        if (existing is not null)
            return existing;

        existing = new ResolutionCombatProfile
        {
            ClientWidth = clientWidth,
            ClientHeight = clientHeight
        };
        _store.CombatProfiles.Add(existing);
        return existing;
    }

    private static ResolutionCombatProfile CloneProfile(ResolutionCombatProfile source) =>
        new()
        {
            ClientWidth = source.ClientWidth,
            ClientHeight = source.ClientHeight,
            CombatRecognition = CloneCombat(source.CombatRecognition),
            AvatarRegion = CloneRegion(source.AvatarRegion),
            Source = source.Source,
            AvatarSource = source.AvatarSource,
            UpdatedAt = source.UpdatedAt
        };

    private static CombatRecognitionConfig CloneCombat(CombatRecognitionConfig source) =>
        JsonSerializer.Deserialize<CombatRecognitionConfig>(JsonSerializer.Serialize(source)) ?? new CombatRecognitionConfig();

    private static OcrRegionConfig CloneRegion(OcrRegionConfig source) =>
        JsonSerializer.Deserialize<OcrRegionConfig>(JsonSerializer.Serialize(source)) ?? CreateDefaultAvatarRegion();

    private static OcrRegionConfig CreateDefaultAvatarRegion() =>
        new() { X = 6, Y = 6, Width = 224, Height = 86 };

    private sealed class ResolutionProfileStore
    {
        public int Version { get; set; } = 1;
        public List<ResolutionCombatProfile> CombatProfiles { get; set; } = new();
    }
}
