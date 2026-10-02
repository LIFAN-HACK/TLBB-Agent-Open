using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.Modules.Billing;

public static class BillingModule
{
  public const string Name = "Billing";

  public static IServiceCollection AddBillingModule(
    this IServiceCollection services,
    IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);

    return services.AddServerModule(
      Name,
      "Products, orders, payments, refunds, and subscription accounting.");
  }
}
