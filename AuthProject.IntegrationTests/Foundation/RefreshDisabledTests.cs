using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
public sealed class RefreshDisabledTests
{
    [Theory]
    [InlineData("csrf", "GET")]
    [InlineData("refresh", "POST")]
    [InlineData("logout", "POST")]
    public async Task DisabledModule_FailsClosed(string route, string method)
    {
        await using var factory = FoundationFactory.Create();
        using var client = factory.Client();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/auth/" + route);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("SERVICE_UNAVAILABLE", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
