using System.Security.Cryptography;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class FourCityTransferRouteService : IFourCityTransferRouteService
{
    private const string ExpectedSha256 = "4d015e7c2bea4414500262517eda625d2fda4dc2e86a142497df024dd6932fe4";
    private readonly Lazy<FourCityRouteAsset> _asset;

    public FourCityTransferRouteService()
    {
        _asset = new Lazy<FourCityRouteAsset>(LoadCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public FourCityRouteAsset Load() => _asset.Value;

    public IReadOnlyList<NavigationRouteDryRun> BuildDryRuns()
    {
        var asset = Load();
        if (!asset.IsValid)
            return Array.Empty<NavigationRouteDryRun>();

        return asset.Legs
            .Select(leg => new NavigationRouteDryRun(
                leg.From,
                leg.To,
                NavigationRouteAuthorizationStatus.RouteNotAuthorized,
                false,
                0,
                "RouteNotAuthorized: route evidence is capture_required; dry-run/noop only."))
            .ToArray();
    }

    private static FourCityRouteAsset LoadCore()
    {
        var path = ResolveAssetPath();
        if (!File.Exists(path))
            return Invalid("Four-city route asset is missing.");

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (!hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return Invalid($"Four-city route hash mismatch: {hash}.", hash);

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var assetId = root.GetProperty("assetId").GetString() ?? "";
            var runtimeAuthorized = root.GetProperty("runtimeAuthorized").GetBoolean();
            var safetyDefault = root.GetProperty("safetyDefault").GetString() ?? "";
            var cities = root.GetProperty("cities").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString() ?? "")
                .Where(item => item.Length > 0)
                .ToArray();
            var legs = root.GetProperty("directedLegs").EnumerateArray()
                .Select(item => new FourCityRouteLeg(
                    item.GetProperty("from").GetString() ?? "",
                    item.GetProperty("to").GetString() ?? "",
                    item.GetProperty("status").GetString() ?? ""))
                .ToArray();
            var evidence = root.GetProperty("requiredEvidencePerLeg").EnumerateArray()
                .Select(item => item.GetString() ?? "")
                .Where(item => item.Length > 0)
                .ToArray();
            var valid = assetId == "four_city_transfer_v1"
                        && !runtimeAuthorized
                        && safetyDefault == "noop"
                        && cities.Distinct(StringComparer.Ordinal).Count() == 4
                        && legs.Length == 12
                        && legs.All(leg => leg.SourceStatus == "capture_required"
                                           && cities.Contains(leg.From, StringComparer.Ordinal)
                                           && cities.Contains(leg.To, StringComparer.Ordinal)
                                           && leg.From != leg.To);
            return new FourCityRouteAsset(
                valid,
                assetId,
                hash,
                runtimeAuthorized,
                safetyDefault,
                cities,
                legs,
                evidence,
                valid
                    ? "Four-city route skeleton verified; all 12 directed legs remain dry-run/noop."
                    : "Four-city route safety contract is invalid.");
        }
        catch (Exception ex)
        {
            return Invalid($"Four-city route parse failed: {ex.Message}");
        }
    }

    private static string ResolveAssetPath()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "data", "runtime", "navigation", "four_city_transfer_v1.json");
        if (File.Exists(packaged))
            return packaged;
        return Path.Combine(
            Environment.CurrentDirectory,
            "src",
            "TlbbAgent.Core",
            "data",
            "runtime",
            "navigation",
            "four_city_transfer_v1.json");
    }

    private static FourCityRouteAsset Invalid(string message, string hash = "") =>
        new(
            false,
            "four_city_transfer_v1",
            hash,
            false,
            "noop",
            Array.Empty<string>(),
            Array.Empty<FourCityRouteLeg>(),
            Array.Empty<string>(),
            message);
}
