using System.Net.Http.Json;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration"), Trait("Dependency", "None")]
public sealed class MfaDisabledTests
{
    [Theory]
    [InlineData("email/verify")] [InlineData("email/resend")] [InlineData("recovery/verify")]
    public async Task Disabled_public_MFA_fails_closed_without_database(string route)
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        object body = route == "email/resend" ? new { challengeId = Guid.NewGuid() }
            : new { challengeId = Guid.NewGuid(), code = route == "email/verify" ? "123456" : new string('A', 43) };
        using var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/" + route, body);
        await ErrorContractTests.AssertProblem(response, 503, "SERVICE_UNAVAILABLE");
    }
    [Theory]
    [InlineData("email/enroll")] [InlineData("email/enable")] [InlineData("email/disable")]
    [InlineData("email/step-up")] [InlineData("recovery/regenerate")]
    public async Task Management_requires_bearer(string route)
    {
        await using var host = FoundationFactory.Create(); using var client = host.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/" + route, new { password = "test password" });
        await ErrorContractTests.AssertProblem(response, 401, "AUTH_UNAUTHENTICATED");
    }
}
