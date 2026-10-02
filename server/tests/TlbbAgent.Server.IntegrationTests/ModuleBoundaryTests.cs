using System.Reflection;
using TlbbAgent.Server.Modules.Audit;
using TlbbAgent.Server.Modules.Billing;
using TlbbAgent.Server.Modules.Entitlement;
using TlbbAgent.Server.Modules.Identity;
using TlbbAgent.Server.Modules.Licensing;
using TlbbAgent.Server.Modules.Release;

namespace TlbbAgent.Server.IntegrationTests;

public sealed class ModuleBoundaryTests
{
  private static readonly Assembly[] ModuleAssemblies =
  [
    typeof(IdentityModule).Assembly,
    typeof(BillingModule).Assembly,
    typeof(EntitlementModule).Assembly,
    typeof(LicensingModule).Assembly,
    typeof(ReleaseModule).Assembly,
    typeof(AuditModule).Assembly,
  ];

  [Fact]
  public void BusinessModulesDoNotReferenceEachOther()
  {
    foreach (var assembly in ModuleAssemblies)
    {
      var forbiddenReferences = assembly
        .GetReferencedAssemblies()
        .Where(reference =>
          reference.Name?.StartsWith("TlbbAgent.Server.Modules.", StringComparison.Ordinal) is true)
        .Select(reference => reference.Name)
        .ToArray();

      Assert.True(
        forbiddenReferences.Length == 0,
        $"{assembly.GetName().Name} references module(s): {string.Join(", ", forbiddenReferences)}");
    }
  }
}
