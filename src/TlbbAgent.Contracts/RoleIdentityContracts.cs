namespace TlbbAgent.Contracts;

public enum RoleIdentityResolutionStatus
{
    Matched = 0,
    NeedsEnrollment,
    Ambiguous,
    Unavailable
}

public sealed record RoleIdentityCandidate(
    string CharacterProfileId,
    string DisplayName,
    string ProfileName,
    double Score);

public sealed record RoleIdentityObservation(
    string ServerKey,
    string SuggestedName,
    string PortraitHash,
    string NamePlateHash,
    int SampleCount,
    double Quality,
    string EvidenceSummary);

public sealed record CharacterIdentityRecord(
    string CharacterProfileId,
    string DisplayName,
    string ProfileName,
    string ServerKey,
    string PortraitHash,
    string NamePlateHash,
    string RecognizedName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

public sealed record RoleIdentityResolution(
    RoleIdentityResolutionStatus Status,
    RoleIdentityObservation Observation,
    CharacterIdentityRecord? Match,
    IReadOnlyList<RoleIdentityCandidate> Candidates,
    string Reason)
{
    public bool IsMatched => Status == RoleIdentityResolutionStatus.Matched && Match is not null;
}

public interface IRoleIdentityService
{
    IReadOnlyList<CharacterIdentityRecord> Identities { get; }

    Task<RoleIdentityResolution> ResolveAsync(
        BoundGameWindow window,
        CancellationToken cancellationToken = default);

    Task<RoleIdentityResolution> EnrollAsync(
        BoundGameWindow window,
        string displayName,
        string profileName,
        CancellationToken cancellationToken = default);

    Task<RoleIdentityResolution> ConfirmExistingAsync(
        BoundGameWindow window,
        string characterProfileId,
        CancellationToken cancellationToken = default);
}
