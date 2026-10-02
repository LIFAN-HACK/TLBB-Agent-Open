using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Release;

public static class ReleaseModule
{
  public const string Name = "Release";

  public static IServiceCollection AddReleaseModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Application and model versions, channels, rollouts, and revocations.");
  }
}
