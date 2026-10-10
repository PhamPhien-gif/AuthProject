using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class RefreshSwaggerTests
{
    [Fact]
    public async Task Swagger_ListsCsrfHeaderAndAnonymousRefreshWithoutBearerRequirement()
    {
        await using var host = FoundationFactory.Create("Development");
        using var client = host.Client();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var route in new[] { "/api/v1/auth/login", "/api/v1/auth/refresh", "/api/v1/auth/logout", "/api/v1/auth/logout-all" })
        {
            var operation = document.GetProperty("paths").GetProperty(route).GetProperty("post");
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "X-CSRF-TOKEN" && p.GetProperty("in").GetString() == "header");
            if (route != "/api/v1/auth/logout-all") Assert.Empty(operation.GetProperty("security").EnumerateArray());
        }
    }
}
