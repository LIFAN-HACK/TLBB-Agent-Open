namespace TlbbAgent.Contracts;

public enum NavigationRouteAuthorizationStatus
{
    Unknown = 0,
    RouteNotAuthorized
}

public sealed record FourCityRouteLeg(
    string From,
    string To,
    string SourceStatus);

public sealed record FourCityRouteAsset(
    bool IsValid,
    string AssetId,
    string Sha256,
    bool RuntimeAuthorized,
    string SafetyDefault,
    IReadOnlyList<string> CityIds,
    IReadOnlyList<FourCityRouteLeg> Legs,
    IReadOnlyList<string> RequiredEvidence,
    string Message);

public sealed record NavigationRouteDryRun(
    string From,
    string To,
    NavigationRouteAuthorizationStatus Status,
    bool RuntimeAuthorized,
    int PlannedInputActions,
    string Reason);

public interface IFourCityTransferRouteService
{
    FourCityRouteAsset Load();
    IReadOnlyList<NavigationRouteDryRun> BuildDryRuns();
}
