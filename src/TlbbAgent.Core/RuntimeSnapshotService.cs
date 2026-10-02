using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class RuntimeSnapshotService : IRuntimeSnapshotService
{
    private readonly IReadOnlyList<IRuntimeStateProvider> _providers;
    private readonly ILogService _log;

    public RuntimeSnapshotService(IEnumerable<IRuntimeStateProvider> providers, ILogService log)
    {
        _providers = providers.ToArray();
        _log = log;
    }

    public RuntimeSnapshot Read(BoundGameWindow window, RuntimeStateProviderKind preferredProvider)
    {
        var preferred = _providers.FirstOrDefault(p => p.Kind == preferredProvider);
        if (preferred is not null)
        {
            var snapshot = SafeRead(preferred, window);
            if (CanUseForRuntimeDecision(snapshot))
                return snapshot;
        }

        foreach (var provider in _providers.Where(p => p.Kind != preferredProvider))
        {
            var snapshot = SafeRead(provider, window);
            if (CanUseForRuntimeDecision(snapshot))
                return snapshot;
        }

        return new RuntimeSnapshot
        {
            Provider = RuntimeStateProviderKind.Unknown,
            Source = "none",
            Quality = RuntimeSnapshotQuality.Unavailable,
            Message = "所有运行快照来源均不可用。"
        };
    }

    public IReadOnlyList<RuntimeSnapshot> ReadAll(BoundGameWindow window) =>
        _providers.Select(provider => SafeRead(provider, window)).ToArray();

    private RuntimeSnapshot SafeRead(IRuntimeStateProvider provider, BoundGameWindow window)
    {
        try
        {
            return provider.Read(window);
        }
        catch (Exception ex)
        {
            _log.Warn("运行快照", $"{provider.Kind} 读取失败：{ex.Message}", window.Id);
            return new RuntimeSnapshot
            {
                Provider = provider.Kind,
                Source = provider.Kind.ToString(),
                Quality = RuntimeSnapshotQuality.Unavailable,
                Message = ex.Message
            };
        }
    }

    private static bool CanUseForRuntimeDecision(RuntimeSnapshot snapshot)
    {
        if (snapshot.Quality == RuntimeSnapshotQuality.Unavailable)
            return false;

        if (snapshot.Provider == RuntimeStateProviderKind.ReadOnlyProcess && !HasBusinessFields(snapshot))
            return false;

        return true;
    }

    private static bool HasBusinessFields(RuntimeSnapshot snapshot) =>
        snapshot.Player.Quality != RuntimeSnapshotQuality.Unavailable
        || snapshot.Target.Quality != RuntimeSnapshotQuality.Unavailable
        || snapshot.Pet.Quality != RuntimeSnapshotQuality.Unavailable
        || snapshot.Scene.Quality != RuntimeSnapshotQuality.Unavailable
        || snapshot.Inventory.Quality != RuntimeSnapshotQuality.Unavailable
        || snapshot.Player.HpPercent is not null
        || snapshot.Player.MpPercent is not null
        || snapshot.Player.IsDead is not null
        || snapshot.Target.HpPercent is not null
        || snapshot.Target.IsPlayer is not null
        || snapshot.Target.IsAttackable is not null
        || snapshot.Pet.HpPercent is not null
        || snapshot.Pet.IsSummoned is not null
        || !string.IsNullOrWhiteSpace(snapshot.Scene.MapName)
        || snapshot.Scene.X is not null
        || snapshot.Scene.Y is not null
        || snapshot.Inventory.IsOpen is not null
        || snapshot.Inventory.UsedSlots is not null
        || snapshot.Inventory.TotalSlots is not null
        || snapshot.Inventory.Candidates.Count > 0;
}
