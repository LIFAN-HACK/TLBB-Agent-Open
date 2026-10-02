using Microsoft.Extensions.Diagnostics.HealthChecks;
using TlbbAgent.Server.Api.Health;
using TlbbAgent.Server.Modules.Audit;
using TlbbAgent.Server.Modules.Billing;
using TlbbAgent.Server.Modules.Entitlement;
using TlbbAgent.Server.Modules.Identity;
using TlbbAgent.Server.Modules.Licensing;
using TlbbAgent.Server.Modules.Release;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services
  .AddIdentityModule(builder.Configuration)
  .AddBillingModule(builder.Configuration)
  .AddEntitlementModule(builder.Configuration)
  .AddLicensingModule(builder.Configuration)
  .AddReleaseModule(builder.Configuration)
  .AddAuditModule(builder.Configuration);
builder.Services
  .AddHealthChecks()
  .AddCheck<ModuleCatalogHealthCheck>(
    "module_catalog",
    failureStatus: HealthStatus.Unhealthy,
    tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.MapServerHealthEndpoints();

app.Run();

public partial class Program;
