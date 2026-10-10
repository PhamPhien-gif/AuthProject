using System.Net.Http.Json;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class PasswordRecoveryDisabledTests
{
    [Theory]
    [InlineData("forgot")]
    [InlineData("reset")]
    public async Task DisabledPublicRecovery_FailsClosed(string route)
    {
        await using var host = FoundationFactory.Create();
        using var client = host.Client();
        object body = route == "forgot" ? new { email = "user@example.test" } : new { token = new string('A', 43), newPassword = "a long secure password 123!" };
        using var response = await client.PostAsJsonAsync("/api/v1/auth/password/" + route, body);
        await ErrorContractTests.AssertProblem(response, 503, "SERVICE_UNAVAILABLE");
    }
    [Theory]
    [InlineData("password/change")]
    [InlineData("reauthenticate")]
    public async Task ProtectedPasswordEndpoints_RequireBearer(string route)
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/" + route, new { password = "test password" });
        await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
}
