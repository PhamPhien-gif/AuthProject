using AuthProject.IntegrationTests.Hosting;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class AdminFoundationTests
{
    [Theory]
    [InlineData("/api/v1/admin/users")] [InlineData("/api/v1/admin/roles")]
    public async Task Admin_reads_require_authentication_without_accessing_disabled_persistence(string route)
    {
        await using var host = new AuthWebApplicationFactory(); using var client = host.CreateHttpsClient();
        using var response = await client.GetAsync(route); await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
    [Fact]
    public async Task Profile_write_requires_authentication()
    {
        await using var host = new AuthWebApplicationFactory(); using var client = host.CreateHttpsClient();
        using var response = await client.PatchAsync("/api/v1/users/me", null); await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
    [Fact]
    public async Task Bootstrap_is_not_a_public_HTTP_endpoint()
    {
        await using var host = new AuthWebApplicationFactory(); using var client = host.CreateHttpsClient();
        using var response = await client.PostAsync("/api/v1/admin/bootstrap", null); await ErrorContractTests.AssertProblem(response, 404, "RESOURCE_NOT_FOUND");
    }
}
