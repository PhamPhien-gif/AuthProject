using AuthProject.IntegrationTests.Hosting;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class AuditFoundationTests
{
    [Theory]
    [InlineData("/api/v1/admin/audit-logs")] [InlineData("/api/v1/users/me/login-history")]
    public async Task Audit_routes_require_auth_without_accessing_disabled_store(string route)
    {
        await using var host = new AuthWebApplicationFactory(); using var client = host.CreateHttpsClient();
        using var response = await client.GetAsync(route); await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
}
