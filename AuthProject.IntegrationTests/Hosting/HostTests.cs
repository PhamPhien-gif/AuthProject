using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthProject.IntegrationTests.Hosting;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class HostTests
{
    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnknownRoute_WhenHostStarts_Returns404(string environment)
    {
        await using var factory = new AuthWebApplicationFactory(environment);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/route-that-does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        Assert.Same(factory.Clock, factory.Services.GetRequiredService<TimeProvider>());
        Assert.Equal(string.Empty, factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
    }

    [Theory]
    [InlineData("Development", "/swagger/index.html", HttpStatusCode.OK)]
    [InlineData("Development", "/swagger/v1/swagger.json", HttpStatusCode.OK)]
    [InlineData("Testing", "/swagger/index.html", HttpStatusCode.NotFound)]
    [InlineData("Testing", "/swagger/v1/swagger.json", HttpStatusCode.NotFound)]
    [InlineData("Production", "/swagger/index.html", HttpStatusCode.NotFound)]
    [InlineData("Production", "/swagger/v1/swagger.json", HttpStatusCode.NotFound)]
    public async Task Swagger_WhenEnvironmentChanges_IsOnlyAvailableInDevelopment(
        string environment, string path, HttpStatusCode expected)
    {
        await using var factory = new AuthWebApplicationFactory(environment);
        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK && path.EndsWith(".json", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(document.RootElement.TryGetProperty("openapi", out _));
            Assert.True(document.RootElement.TryGetProperty("paths", out _));
        }
    }
}
