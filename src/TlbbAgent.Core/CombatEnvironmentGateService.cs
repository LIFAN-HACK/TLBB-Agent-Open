using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class CombatEnvironmentGateService : ICombatEnvironmentGateService
{
    private const int CacheMs = 350;

    private readonly IPerceptionHub _perception;
    private readonly ILogService _log;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SceneObservation> _lastReliableScene = new(StringComparer.Ordinal);

    public CombatEnvironmentGateService(IPerceptionHub perception, ILogService log)
    {
        _perception = perception;
        _log = log;
    }

    public CombatEnvironmentGateResult Evaluate(BoundGameWindow window, GlobalConfig config)
    {
        var key = string.IsNullOrWhiteSpace(window.Id) ? window.Hwnd.ToString() : window.Id;
        var now = Environment.TickCount64;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached) && now - cached.Ticks <= CacheMs)
                return cached.Result;
        }

        var result = EvaluateFresh(window, config, key, now);
        lock (_cache)
            _cache[key] = new CacheEntry(now, result);

        return result;
    }

    private CombatEnvironmentGateResult EvaluateFresh(BoundGameWindow window, GlobalConfig config, string key, long now)
    {
        try
        {
            var minimap = _perception.Read(window, config).Minimap;
            var scene = ToSceneSnapshot(minimap);
            if (!IsReliableScene(scene))
                return CombatEnvironmentGateResult.Allow(scene.GateReason);

            var mapName = scene.MapName ?? "";
            var normalizedMap = NormalizeMapName(mapName);
            var movement = EvaluateMovementAndSceneSwitch(key, scene, normalizedMap, now);
            if (movement.ShouldSkipCombat)
                return movement;

            // Safe-map recognition remains advisory until the map model is stable
            // enough to make it a hard combat gate.
            if (IsSafeMap(mapName))
                return CombatEnvironmentGateResult.Allow($"当前地图 {mapName} 可能为安全区（仅记录，不暂停打怪）。");

            return CombatEnvironmentGateResult.Allow($"当前地图 {mapName}。");
        }
        catch (Exception ex)
        {
            _log.Warn("环境门禁", $"读取场景门禁失败：{ex.Message}", window.Id);
            return CombatEnvironmentGateResult.Allow(ex.Message);
        }
    }

    private static SceneRuntimeSnapshot ToSceneSnapshot(MinimapPerception minimap)
    {
        var available = minimap.Source != PerceptionSourceKind.Unavailable;
        var ready = available
                    && !string.IsNullOrWhiteSpace(minimap.MapName)
                    && minimap.X is not null
                    && minimap.Y is not null;

        return new SceneRuntimeSnapshot
        {
            Quality = ready
                ? RuntimeSnapshotQuality.Reliable
                : available
                    ? RuntimeSnapshotQuality.Partial
                    : RuntimeSnapshotQuality.Unavailable,
            Source = available ? "vision/minimap" : "vision/minimap-unavailable",
            ConfidencePercent = ready ? 85 : available ? 60 : 0,
            SceneUsable = ready,
            MapFieldReady = !string.IsNullOrWhiteSpace(minimap.MapName),
            CoordinateFieldReady = minimap.X is not null && minimap.Y is not null,
            GateReason = available ? string.Empty : "小地图模型尚未接入。",
            MapName = minimap.MapName,
            MapNameSource = available ? "vision/minimap" : string.Empty,
            MapNameConfidencePercent = ready ? 85 : available ? 60 : 0,
            X = minimap.X,
            Y = minimap.Y,
            CoordinateSource = available ? "vision/minimap" : string.Empty,
            CoordinateConfidencePercent = ready ? 85 : available ? 60 : 0,
            IsDungeon = minimap.IsDungeon
        };
    }

    private CombatEnvironmentGateResult EvaluateMovementAndSceneSwitch(
        string key,
        SceneRuntimeSnapshot scene,
        string normalizedMap,
        long now)
    {
        var current = new SceneObservation(
            normalizedMap,
            scene.MapName ?? "",
            scene.X ?? 0,
            scene.Y ?? 0,
            now,
            scene.Quality,
            scene.ConfidencePercent);

        SceneObservation? previous;
        lock (_cache)
            _lastReliableScene.TryGetValue(key, out previous);

        if (previous is null)
        {
            lock (_cache)
                _lastReliableScene[key] = current;
            return CombatEnvironmentGateResult.Allow("首次可靠场景样本。");
        }

        lock (_cache)
            _lastReliableScene[key] = current;

        if (!string.Equals(previous.NormalizedMap, current.NormalizedMap, StringComparison.Ordinal))
        {
            return CombatEnvironmentGateResult.Allow(
                $"检测到地图从 {previous.DisplayMap} 变化为 {current.DisplayMap}；未执行自动寻路，继续打怪。");
        }

        return CombatEnvironmentGateResult.Allow("场景未切换，允许继续打怪。");
    }

    private static bool IsReliableScene(SceneRuntimeSnapshot scene)
    {
        if (scene.Quality != RuntimeSnapshotQuality.Reliable || !scene.SceneUsable)
            return false;
        if (!scene.MapFieldReady || !scene.CoordinateFieldReady)
            return false;
        if (string.IsNullOrWhiteSpace(scene.MapName))
            return false;
        if (string.Equals(scene.MapNameSource, "manual-confirmed-map", StringComparison.OrdinalIgnoreCase))
            return false;

        return scene.MapNameConfidencePercent >= 70 && scene.CoordinateConfidencePercent >= 60;
    }

    private static bool IsSafeMap(string mapName)
    {
        var normalized = NormalizeMapName(mapName);
        if (normalized.Length < 2)
            return false;

        var excluded = new[] { "楼兰寻宝" };
        if (excluded.Any(k => normalized.Contains(k, StringComparison.Ordinal)))
            return false;

        var safeMaps = new[] { "苏州", "洛阳", "大理", "楼兰", "束河", "帮会城市" };
        return safeMaps.Any(k => normalized.Contains(k, StringComparison.Ordinal));
    }

    private static string NormalizeMapName(string mapName) =>
        new(mapName.Where(c => c is >= '\u4E00' and <= '\u9FFF' || char.IsDigit(c)).ToArray());

    private sealed record CacheEntry(long Ticks, CombatEnvironmentGateResult Result);

    private sealed record SceneObservation(
        string NormalizedMap,
        string DisplayMap,
        int X,
        int Y,
        long Ticks,
        RuntimeSnapshotQuality Quality,
        int ConfidencePercent);

}
