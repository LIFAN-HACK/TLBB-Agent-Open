using Microsoft.Extensions.Diagnostics.HealthChecks;
using TlbbAgent.Server.Modules.Audit;
using TlbbAgent.Server.Modules.Billing;
using TlbbAgent.Server.Modules.Entitlement;
using TlbbAgent.Server.Modules.Identity;
using TlbbAgent.Server.Modules.Licensing;
using TlbbAgent.Server.Modules.Release;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Api.Health;

public sealed class ModuleCatalogHealthCheck : IHealthCheck
{
  private static readonly HashSet<string> RequiredModuleNames = new(
    [
      IdentityModule.Name,
      BillingModule.Name,
      EntitlementModule.Name,
      LicensingModule.Name,
      ReleaseModule.Name,
      AuditModule.Name,
    ],
    StringComparer.Ordinal);

  private readonly ModuleDescriptor[] _modules;

  public ModuleCatalogHealthCheck(IEnumerable<ModuleDescriptor> modules)
  {
    ArgumentNullException.ThrowIfNull(modules);
    _modules = modules.ToArray();
  }

  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(context);
    cancellationToken.ThrowIfCancellationRequested();

    var names = _modules.Select(module => module.Name).ToArray();
    var duplicateNames = names
      .GroupBy(name => name, StringComparer.Ordinal)
      .Where(group => group.Count() > 1)
      .Select(group => group.Key)
      .Order(StringComparer.Ordinal)
      .ToArray();
    var missingNames = RequiredModuleNames
      .Except(names, StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)
      .ToArray();
    var unexpectedNames = names
      .Except(RequiredModuleNames, StringComparer.Ordinal)
      .Order(StringComparer.Ordinal)
      .ToArray();

    if (duplicateNames.Length > 0 || missingNames.Length > 0 || unexpectedNames.Length > 0)
    {
      IReadOnlyDictionary<string, object> data = new Dictionary<string, object>
      {
        ["duplicates"] = string.Join(',', duplicateNames),
        ["missing"] = string.Join(',', missingNames),
        ["unexpected"] = string.Join(',', unexpectedNames),
      };

      return Task.FromResult(HealthCheckResult.Unhealthy(
        "The server module catalog is invalid.",
        data: data));
    }

    return Task.FromResult(HealthCheckResult.Healthy(
      $"All {_modules.Length} required server modules are loaded."));
  }
}
