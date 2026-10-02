using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Identity;

public static class IdentityModule
{
  public const string Name = "Identity";

  public static IServiceCollection AddIdentityModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Accounts, authentication, sessions, and security policy.");
  }
}
