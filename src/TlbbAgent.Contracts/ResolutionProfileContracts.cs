namespace TlbbAgent.Contracts;

public sealed class ResolutionCombatProfile
{
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public CombatRecognitionConfig CombatRecognition { get; set; } = new();
    public OcrRegionConfig AvatarRegion { get; set; } = new() { X = 6, Y = 6, Width = 224, Height = 86 };
    public string Source { get; set; } = "";
    public string AvatarSource { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public interface IResolutionProfileService
{
    IReadOnlyList<ResolutionCombatProfile> CombatProfiles { get; }
    CombatRecognitionConfig ResolveCombat(int clientWidth, int clientHeight, CombatRecognitionConfig fallback);
    OcrRegionConfig ResolveAvatar(int clientWidth, int clientHeight, OcrRegionConfig fallback);
    void SaveCombatProfile(int clientWidth, int clientHeight, CombatRecognitionConfig config, string source);
    void SaveAvatarProfile(int clientWidth, int clientHeight, OcrRegionConfig avatarRegion, string source);
    bool TryGetCombatProfile(int clientWidth, int clientHeight, out ResolutionCombatProfile profile);
    bool DeleteCombatProfile(int clientWidth, int clientHeight);
    void Reload();
}
