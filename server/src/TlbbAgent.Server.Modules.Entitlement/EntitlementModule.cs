using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Entitlement;

public static class EntitlementModule
{
  public const string Name = "Entitlement";

  public static IServiceCollection AddEntitlementModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Effective product and feature rights derived from trusted facts.");
  }
}
