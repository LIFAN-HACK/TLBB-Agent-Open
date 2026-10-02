using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Licensing;

public static class LicensingModule
{
  public const string Name = "Licensing";

  public static IServiceCollection AddLicensingModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Device registration, concurrent-use leases, and model-key grants.");
  }
}
