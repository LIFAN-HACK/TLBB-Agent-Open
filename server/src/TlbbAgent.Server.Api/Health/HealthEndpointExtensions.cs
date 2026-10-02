using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TlbbAgent.Server.Api.Health;

public static class HealthEndpointExtensions
{
  public static IEndpointRouteBuilder MapServerHealthEndpoints(
    this IEndpointRouteBuilder endpoints)
  {
    ArgumentNullException.ThrowIfNull(endpoints);

    endpoints.MapHealthChecks(
      "/health/live",
      new HealthCheckOptions
      {
        AllowCachingResponses = false,
        Predicate = _ => false,
        ResponseWriter = WriteResponseAsync,
      });

    endpoints.MapHealthChecks(
      "/health/ready",
      new HealthCheckOptions
      {
        AllowCachingResponses = false,
        Predicate = registration => registration.Tags.Contains("ready"),
        ResponseWriter = WriteResponseAsync,
      });

    return endpoints;
  }

  private static Task WriteResponseAsync(HttpContext context, HealthReport report)
  {
    var response = new HealthResponse(report.Status.ToString());

    return context.Response.WriteAsJsonAsync(
      response,
      cancellationToken: context.RequestAborted);
  }

  private sealed record HealthResponse(string Status);
}
