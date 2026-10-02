using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TlbbAgent.Server.IntegrationTests;

public sealed class HealthEndpointsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
  [Theory]
  [InlineData("/health/live")]
  [InlineData("/health/ready")]
  public async Task HealthEndpointReturnsMinimalHealthyResponse(string path)
  {
    using var client = factory.CreateClient(
      new WebApplicationFactoryClientOptions
      {
        AllowAutoRedirect = false,
      });
    using var response = await client.GetAsync(path);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

    await using var body = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(body);

    Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());
    Assert.Single(document.RootElement.EnumerateObject());
  }
}
