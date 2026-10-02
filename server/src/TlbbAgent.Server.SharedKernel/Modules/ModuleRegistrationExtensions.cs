using Microsoft.Extensions.DependencyInjection;

namespace TlbbAgent.Server.SharedKernel.Modules;

public static class ModuleRegistrationExtensions
{
  public static IServiceCollection AddServerModule(
    this IServiceCollection services,
    string name,
    string description)
  {
    ArgumentNullException.ThrowIfNull(services);

    services.AddSingleton(new ModuleDescriptor(name, description));
    return services;
  }
}
