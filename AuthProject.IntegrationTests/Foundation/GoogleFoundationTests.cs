using System.Net.Http.Json;
using AuthProject.DTOs;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class GoogleFoundationTests
{
    [Fact]
    public async Task Disabled_Google_start_and_complete_fail_closed()
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        using var start = await client.GetAsync("/api/v1/auth/google/start"); await ErrorContractTests.AssertProblem(start, 503, "SERVICE_UNAVAILABLE");
        using var complete = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(new string('A', 43)));
        await ErrorContractTests.AssertProblem(complete, 503, "SERVICE_UNAVAILABLE");
    }
    [Theory]
    [InlineData("link")] [InlineData("reauthenticate")]
    public async Task Management_requires_bearer(string route)
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        using var response = await client.PostAsync("/api/v1/auth/google/" + route, null); await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
    [Fact]
    public async Task Unlink_requires_bearer()
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        using var response = await client.DeleteAsync("/api/v1/auth/google/link"); await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
}
