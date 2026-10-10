using System.Net.Http.Json;
using AuthProject.IntegrationTests.Hosting;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class RegistrationDisabledTests
{
    [Theory]
    [InlineData("register")]
    [InlineData("email/resend")]
    [InlineData("email/verify")]
    public async Task DisabledRegistration_ReturnsSafe503WithoutAccessingSql(string path)
    {
        await using var factory = new AuthWebApplicationFactory();
        using var client = factory.CreateHttpsClient();
        object body = path switch
        {
            "register" => new { email = "person@example.test", password = "a long secure password 123!", displayName = "User" },
            "email/resend" => new { email = "person@example.test" },
            _ => new { token = AuthProject.Security.EmailVerificationToken.Create() }
        };
        using var response = await client.PostAsJsonAsync("/api/v1/auth/" + path, body);
        await ErrorContractTests.AssertProblem(response, 503, "SERVICE_UNAVAILABLE");
    }

    [Fact]
    public async Task Swagger_ContainsFiveImplementedAuthOperationsAndBearerDefinition()
    {
        await using var factory = new AuthWebApplicationFactory("Development");
        using var client = factory.CreateHttpsClient();
        var schema = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        var paths = schema.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/auth/register", out _));
        Assert.True(paths.TryGetProperty("/api/v1/auth/email/verify", out _));
        Assert.True(paths.TryGetProperty("/api/v1/auth/email/resend", out _));
        Assert.True(paths.TryGetProperty("/api/v1/auth/login", out _));
        Assert.True(paths.TryGetProperty("/api/v1/auth/me", out _));
        Assert.Equal("bearer", schema.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString());
        Assert.Empty(paths.GetProperty("/api/v1/auth/login").GetProperty("post").GetProperty("security").EnumerateArray());
    }
}
