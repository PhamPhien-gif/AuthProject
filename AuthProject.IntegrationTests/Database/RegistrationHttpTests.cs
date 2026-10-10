using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuthProject.BackgroundJobs;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Security;
using AuthProject.Services.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class RegistrationHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(AuthWebApplicationFactory root)
    {
        await using var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true", ["Auth:Registration:Enabled"] = "true",
                ["Auth:Registration:VerificationUrl"] = "https://client.example.test/verify-email",
                ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test", ["Auth:Email:Sender"] = "sender@example.test",
                ["Auth:Email:Username"] = "test-user", ["Auth:Email:Password"] = "test-only-password"
            }));
            builder.ConfigureServices(services =>
            {
                // Dispatch is exercised separately with a fake transport; this host cannot send real SMTP.
                foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(VerificationEmailWorker)).ToArray())
                    services.Remove(descriptor);
            });
        });
    }
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task RegisterVerifyAndResend_UsePublicContractsAndCreateNoTokens()
    {
        await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var factory = await CreateFactoryAsync(root);
        using var client = Client(factory);
        var email = Guid.NewGuid() + "@example.test";
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "a long secure password 123!", displayName = " User " });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        var publicBody = await registered.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", publicBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", registered.Headers.CacheControl!.ToString());
        using var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = email.ToUpperInvariant(), password = "a different secure password 456!", displayName = "Changed" });
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(publicBody, await duplicate.Content.ReadAsStringAsync());
        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/email/resend", new { email = Guid.NewGuid() + "@example.test" });
        Assert.Equal(publicBody, await unknown.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync(x => x.NormalizedEmail == EmailNormalizer.Normalize(email));
        var challenge = await db.Challenges.SingleAsync(x => x.UserId == user.Id);
        var row = await db.OutboxMessages.SingleAsync(x => x.Kind == OutboxKind.Email && x.EventId == challenge.Id);
        var token = scope.ServiceProvider.GetRequiredService<IEmailPayloadProtector>().Unprotect(row.ProtectedPayload).Token;
        using var verified = await client.PostAsJsonAsync("/api/v1/auth/email/verify", new { token });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        using var replay = await client.PostAsJsonAsync("/api/v1/auth/email/verify", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("AUTH_CHALLENGE_INVALID", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("{\"email\":\"person@example.test\",\"password\":\"short\",\"displayName\":\"User\"}", "VALIDATION_FAILED")]
    [InlineData("{\"email\":\"person@example.test\",\"password\":\"a long secure password 123!\",\"displayName\":\"User\",\"role\":\"Admin\"}", "INVALID_REQUEST")]
    [InlineData("{\"email\":\"invalid\",\"password\":\"a long secure password 123!\",\"displayName\":\"User\"}", "VALIDATION_FAILED")]
    public async Task InvalidRegistration_RejectsWeakPasswordEmailAndRoleInjection(string json, string code)
    {
        await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var factory = await CreateFactoryAsync(root);
        using var client = Client(factory);
        using var response = await client.PostAsync("/api/v1/auth/register", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
