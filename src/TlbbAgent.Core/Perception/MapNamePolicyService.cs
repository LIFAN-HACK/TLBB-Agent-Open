using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed record MapNameCandidateEvaluation(
    string RawText,
    string NormalizedText,
    string CanonicalMapName,
    bool CandidateAccepted,
    double Confidence,
    string Source,
    string Reason);

public sealed class MapNamePolicyService
{
    public const string ExpectedSha256 = "9e78400d46c2ce52274730601798a86483af5a9c03325b65422cdc409e43303b";

    private readonly ConcurrentDictionary<string, WindowHistory> _histories = new(StringComparer.Ordinal);
    private readonly MapNamePolicyAsset? _policy;
    private readonly string _assetPath;
    private readonly string _assetSha256;
    private readonly string _loadMessage;

    public MapNamePolicyService()
    {
        _assetPath = ResolveAssetPath();
        (_policy, _assetSha256, _loadMessage) = LoadPolicy(_assetPath);
    }

    public bool IsAvailable => _policy is not null;
    public bool RuntimeAuthorized => _policy?.RuntimeAuthorized == true;
    public string AssetPath => _assetPath;
    public string AssetSha256 => _assetSha256;
    public string AssetVersion => _policy is null ? "" : $"{_policy.ArtifactKey}:v{_policy.Version}";
    public string LoadMessage => _loadMessage;

    public MapNameCandidateEvaluation Evaluate(
        string rawText,
        double confidence,
        string source,
        string sampleId = "")
    {
        var normalizedSource = NormalizeSource(source);
        var normalizedText = NormalizeText(rawText);
        var boundedConfidence = Math.Clamp(confidence, 0, 1);
        if (_policy is null)
        {
            return new MapNameCandidateEvaluation(
                rawText ?? "",
                normalizedText,
                "",
                false,
                boundedConfidence,
                normalizedSource,
                "policy_unavailable");
        }

        if (normalizedSource == "sample_match"
            && !string.IsNullOrWhiteSpace(sampleId)
            && _policy.SampleLabelOverrides.TryGetValue(sampleId.Trim(), out var sampleOverride))
        {
            normalizedText = NormalizeText(sampleOverride.Canonical);
        }

        var threshold = normalizedSource == "sample_match"
            ? _policy.Thresholds.SampleMatchMinimum
            : _policy.Thresholds.OpenTextExactMinimum;
        if (_policy.KnownMapNames.Contains(normalizedText, StringComparer.Ordinal)
            && boundedConfidence >= threshold)
        {
            return new MapNameCandidateEvaluation(
                rawText ?? "",
                normalizedText,
                normalizedText,
                true,
                boundedConfidence,
                normalizedSource,
                "exact");
        }

        if (_policy.ReviewedAliases.TryGetValue(normalizedText, out var alias)
            && _policy.KnownMapNames.Contains(alias.Canonical, StringComparer.Ordinal)
            && boundedConfidence >= _policy.Thresholds.ReviewedAliasMinimum)
        {
            return new MapNameCandidateEvaluation(
                rawText ?? "",
                normalizedText,
                alias.Canonical,
                true,
                boundedConfidence,
                normalizedSource,
                "reviewed_alias");
        }

        return new MapNameCandidateEvaluation(
            rawText ?? "",
            normalizedText,
            "",
            false,
            boundedConfidence,
            normalizedSource,
            "unknown");
    }

    public MapNamePolicyPerception Observe(
        string windowId,
        long observationSequence,
        MapNameCandidateEvaluation candidate)
    {
        if (_policy is null)
            return BuildUnavailable(observationSequence, candidate);

        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        var state = _histories.GetOrAdd(key, _ => new WindowHistory());
        lock (state)
        {
            if (observationSequence <= 0)
            {
                return BuildResult(
                    observationSequence,
                    candidate,
                    stableMapName: "",
                    stable: false,
                    votes: 0,
                    historyCount: state.Observations.Count,
                    stabilityReason: "invalid_frame_sequence");
            }

            if (state.LastObservationSequence > 0
                && observationSequence < state.LastObservationSequence)
            {
                state.Observations.Clear();
                state.LastObservationSequence = 0;
                state.LastCandidate = null;
            }

            if (observationSequence != state.LastObservationSequence)
            {
                state.Observations.Enqueue(new MapNameObservation(observationSequence, candidate));
                while (state.Observations.Count > _policy.Thresholds.StableWindowSize)
                    state.Observations.Dequeue();
                state.LastObservationSequence = observationSequence;
                state.LastCandidate = candidate;
            }
            else if (state.LastCandidate is not null)
            {
                candidate = state.LastCandidate;
            }

            var observations = state.Observations.ToArray();
            var accepted = observations
                .Select((item, index) => new { item.Candidate, Index = index })
                .Where(item => item.Candidate.CandidateAccepted
                               && !string.IsNullOrWhiteSpace(item.Candidate.CanonicalMapName))
                .GroupBy(item => item.Candidate.CanonicalMapName, StringComparer.Ordinal)
                .Select(group => new
                {
                    MapName = group.Key,
                    Votes = group.Count(),
                    FirstIndex = group.Min(item => item.Index)
                })
                .OrderByDescending(item => item.Votes)
                .ThenBy(item => item.FirstIndex)
                .FirstOrDefault();
            var votes = accepted?.Votes ?? 0;
            var stable = accepted is not null && votes >= _policy.Thresholds.StableVotesRequired;
            return BuildResult(
                observationSequence,
                candidate,
                stable ? accepted!.MapName : "",
                stable,
                votes,
                observations.Length,
                stable ? "stable" : "unstable");
        }
    }

    public void Reset(string windowId)
    {
        var key = string.IsNullOrWhiteSpace(windowId) ? "-" : windowId;
        _histories.TryRemove(key, out _);
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character is >= '\u3400' and <= '\u9FFF'
                || character is >= 'A' and <= 'Z'
                || character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private MapNamePolicyPerception BuildResult(
        long observationSequence,
        MapNameCandidateEvaluation candidate,
        string stableMapName,
        bool stable,
        int votes,
        int historyCount,
        string stabilityReason) =>
        new()
        {
            RawText = candidate.RawText,
            NormalizedText = candidate.NormalizedText,
            CanonicalCandidate = candidate.CanonicalMapName,
            StableMapName = stableMapName,
            CandidateSource = candidate.Source,
            CandidateReason = candidate.Reason,
            StabilityReason = stabilityReason,
            Confidence = candidate.Confidence,
            CandidateAccepted = candidate.CandidateAccepted,
            Stable = stable,
            Votes = votes,
            HistoryCount = historyCount,
            ObservationSequence = observationSequence,
            PolicyAvailable = true,
            RuntimeAuthorized = RuntimeAuthorized,
            AssetVersion = AssetVersion,
            AssetSha256 = _assetSha256,
            Message = stable
                ? $"map={stableMapName}, votes={votes}/{historyCount}, source={candidate.Source}"
                : $"map candidate is {stabilityReason}: {candidate.NormalizedText}, votes={votes}/{historyCount}"
        };

    private MapNamePolicyPerception BuildUnavailable(
        long observationSequence,
        MapNameCandidateEvaluation candidate) =>
        new()
        {
            RawText = candidate.RawText,
            NormalizedText = candidate.NormalizedText,
            CandidateSource = candidate.Source,
            CandidateReason = candidate.Reason,
            StabilityReason = "policy_unavailable",
            Confidence = candidate.Confidence,
            ObservationSequence = observationSequence,
            PolicyAvailable = false,
            RuntimeAuthorized = false,
            AssetVersion = AssetVersion,
            AssetSha256 = _assetSha256,
            Message = _loadMessage
        };

    private static string NormalizeSource(string source) =>
        source switch
        {
            "sample_match" => "sample_match",
            "open_text" => "open_text",
            _ => "unknown"
        };

    private static (MapNamePolicyAsset? Policy, string Hash, string Message) LoadPolicy(string path)
    {
        if (!File.Exists(path))
            return (null, "", $"Map-name policy is missing: {path}");

        try
        {
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return (null, hash, $"Map-name policy hash mismatch: {hash}.");

            var policy = JsonSerializer.Deserialize<MapNamePolicyAsset>(bytes, JsonOptions);
            if (policy is null
                || !string.Equals(policy.ArtifactKey, "map_name_policy_v2", StringComparison.Ordinal)
                || policy.Version != 2
                || policy.Thresholds.SampleMatchMinimum is < 0 or > 1
                || policy.Thresholds.OpenTextExactMinimum is < 0 or > 1
                || policy.Thresholds.ReviewedAliasMinimum is < 0 or > 1
                || policy.Thresholds.StableVotesRequired <= 0
                || policy.Thresholds.StableWindowSize < policy.Thresholds.StableVotesRequired
                || policy.KnownMapNames.Count == 0)
            {
                return (null, hash, "Map-name policy contract is invalid.");
            }

            return (policy, hash, "Map-name policy loaded and hash verified.");
        }
        catch (Exception ex)
        {
            return (null, "", $"Map-name policy load failed: {ex.Message}");
        }
    }

    private static string ResolveAssetPath()
    {
        var packaged = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "runtime",
            "perception",
            "map_name_policy_v2",
            "policy.json");
        if (File.Exists(packaged))
            return packaged;

        return Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "TlbbAgent.Core",
            "data",
            "runtime",
            "perception",
            "map_name_policy_v2",
            "policy.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class WindowHistory
    {
        public Queue<MapNameObservation> Observations { get; } = new();
        public long LastObservationSequence { get; set; }
        public MapNameCandidateEvaluation? LastCandidate { get; set; }
    }

    private sealed record MapNameObservation(
        long ObservationSequence,
        MapNameCandidateEvaluation Candidate);

    private sealed class MapNamePolicyAsset
    {
        public string ArtifactKey { get; set; } = "";
        public int Version { get; set; }
        public bool RuntimeAuthorized { get; set; }
        public MapNamePolicyThresholds Thresholds { get; set; } = new();
        public List<string> KnownMapNames { get; set; } = new();
        public Dictionary<string, MapNamePolicyAlias> ReviewedAliases { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, MapNameSampleLabelOverride> SampleLabelOverrides { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class MapNamePolicyThresholds
    {
        public double SampleMatchMinimum { get; set; }
        public double OpenTextExactMinimum { get; set; }
        public double ReviewedAliasMinimum { get; set; }
        public int StableVotesRequired { get; set; }
        public int StableWindowSize { get; set; }
    }

    private sealed class MapNamePolicyAlias
    {
        public string Canonical { get; set; } = "";
    }

    private sealed class MapNameSampleLabelOverride
    {
        public string Canonical { get; set; } = "";
    }
}
