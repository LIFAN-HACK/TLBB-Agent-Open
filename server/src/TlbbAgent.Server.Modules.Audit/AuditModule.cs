using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Audit;

public static class AuditModule
{
  public const string Name = "Audit";

  public static IServiceCollection AddAuditModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Security, financial, and administrative audit events.");
  }
}
