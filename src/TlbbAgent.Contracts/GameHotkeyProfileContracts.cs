namespace TlbbAgent.Contracts;

public enum GameHotkeyProfileStatus
{
    Loaded = 0,
    InvalidRole,
    AccountsRootUnavailable,
    RoleNotFound,
    RoleAmbiguous,
    MappingFileMissing,
    MappingFileInvalid
}

public static class GameInputSemanticIds
{
    public const string TargetSelect = "target.select";
    public const string CharacterPanel = "panel.character";
    public const string InventoryPanel = "panel.inventory";
    public const string SkillPanel = "panel.skill";
    public const string QuestPanel = "panel.quest";
    public const string TradePanel = "panel.trade";
    public const string PetPanel = "panel.pet";
    public const string WorldMap = "panel.world_map";
    public const string AreaMap = "panel.area_map";

    public static string SkillSlot(int slot) => $"skill.slot.{slot}";
}

public sealed record GameHotkeyBinding(
    int ActionId,
    string SemanticId,
    string Hotkey,
    long EncodedCurrentKey,
    bool IsDispatchable,
    string Reason);

public sealed record GameHotkeyProfileLoadResult(
    GameHotkeyProfileStatus Status,
    string RoleName,
    string MappingFingerprint,
    IReadOnlyList<GameHotkeyBinding> Bindings,
    string Reason)
{
    public bool Loaded => Status == GameHotkeyProfileStatus.Loaded;
}

public interface IGameHotkeyProfileService
{
    GameHotkeyProfileLoadResult BindToRole(BoundGameWindow window, string roleName);
    bool TryResolve(nint hwnd, string semanticId, out GameHotkeyBinding binding);
    GameHotkeyProfileLoadResult? GetAttached(nint hwnd);
    void Unbind(nint hwnd);
}
