namespace TlbbAgent.Contracts;

public enum CombatBackendPreference
{
    Auto = 0,
    InternalPlugin,
    VisualModel,
    ExternalOcr = VisualModel
}

public enum CombatBackendKind
{
    VisualModel = 0,
    ExternalOcr = VisualModel,
    InternalPlugin
}

public enum GameBuiltInHangSwitchMode
{
    Disabled = 0,
    Hotkey = 1,
    ClientClick = 2
}

public sealed record CombatBackendStatus(
    CombatBackendKind Kind,
    string DisplayName,
    bool IsAvailable,
    string Message);

public sealed record GameBuiltInHangSwitchResult(
    GameBuiltInHangSwitchMode Mode,
    bool RequestedExecution,
    bool Executed,
    bool WouldExecute,
    bool IsConfigured,
    bool IsValid,
    string Action,
    string Message,
    string Hotkey,
    int? ClientX,
    int? ClientY,
    InputDispatchMode DispatchMode);

public interface ICombatBackend
{
    CombatBackendKind Kind { get; }
    string DisplayName { get; }

    CombatBackendStatus GetStatus(BoundGameWindow window, GlobalConfig config);
    CombatRecognitionResult Read(BoundGameWindow window, GlobalConfig config);
    bool TryParseHotkey(string hotkey, out string error);
    bool TryConsumeGlobalCooldown(int cooldownMs);
    bool TryConsumeGlobalCooldown(BoundGameWindow window, int cooldownMs) =>
        TryConsumeGlobalCooldown(cooldownMs);
    bool CastHotkey(BoundGameWindow window, string hotkey, GlobalConfig config);
    bool ClickClient(BoundGameWindow window, int x, int y, GlobalConfig config);
}

public interface ICombatBackendSelector
{
    ICombatBackend Resolve(BoundGameWindow window, GlobalConfig config, out CombatBackendStatus status);
}

public interface IGameBuiltInHangSwitchService
{
    GameBuiltInHangSwitchResult TryTrigger(BoundGameWindow window, GlobalConfig config, bool execute);
}
