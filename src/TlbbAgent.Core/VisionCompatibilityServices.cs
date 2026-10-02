using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

/// <summary>
/// Compatibility adapter for callers that still depend on ICombatRecognitionService.
/// The implementation reads the new RuntimePerceptionSnapshot and never calls legacy OCR.
/// </summary>
public sealed class VisionCombatRecognitionAdapter : ICombatRecognitionService
{
    private readonly IPerceptionHub _perceptionHub;
    private readonly IGlobalConfigService _global;

    public VisionCombatRecognitionAdapter(IPerceptionHub perceptionHub, IGlobalConfigService global)
    {
        _perceptionHub = perceptionHub;
        _global = global;
    }

    public CombatRecognitionResult Recognize(BoundGameWindow window, CombatRecognitionConfig config)
    {
        var global = _global.Current;
        var includeOpenText = TargetPolicyRuntime.RequiresTargetName(global.Targeting);
        return VisionCombatResultMapper.FromSnapshot(
            _perceptionHub.Read(window, global, includeOpenText));
    }
}

/// <summary>
/// Temporary compatibility implementation for frontend/debug constructors.
/// It makes legacy dictionary OCR explicitly unavailable instead of silently using it.
/// </summary>
public sealed class DisabledLegacyTemplateOcrService : ITemplateOcrService
{
    private static readonly IReadOnlyList<TemplateGlyphInfo> EmptyGlyphs = Array.Empty<TemplateGlyphInfo>();
    private static readonly IReadOnlyCollection<string> EmptySources = Array.Empty<string>();

    public int LoadedGlyphCount => 0;
    public int RenderableGlyphCount => 0;
    public IReadOnlyList<TemplateGlyphInfo> Glyphs => EmptyGlyphs;
    public IReadOnlyList<TemplateGlyphInfo> RenderableGlyphs => EmptyGlyphs;
    public IReadOnlyCollection<string> LoadedSources => EmptySources;

    public int LoadDictionary(string path) => 0;
    public string Recognize(CaptureResult image) => string.Empty;
}
