using Microsoft.Extensions.DependencyInjection;
using TlbbAgent.Server.SharedKernel.Modules;

namespace TlbbAgent.Server.IntegrationTests;

public sealed class ModuleCompositionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
  private static readonly string[] ExpectedModuleNames =
  [
    "Audit",
    "Billing",
    "Entitlement",
    "Identity",
    "Licensing",
    "Release",
  ];

  [Fact]
  public void ApiComposesEachRequiredModuleExactlyOnce()
  {
    var modules = factory.Services.GetServices<ModuleDescriptor>().ToArray();
    var actualNames = modules
      .Select(module => module.Name)
      .Order(StringComparer.Ordinal)
      .ToArray();

    Assert.Equal(ExpectedModuleNames, actualNames);
    Assert.All(modules, module => Assert.False(string.IsNullOrWhiteSpace(module.Description)));
  }
}
