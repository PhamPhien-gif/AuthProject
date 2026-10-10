using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AuthProject.BackgroundJobs;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Foundation;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Services.Email;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class PasswordRecoveryHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string OldPassword = "a long secure password 123!", NewPassword = "a different secure password 456!";
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task<User> Seed(string state = "active")
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active,
            EmailConfirmedAt = DateTimeOffset.UtcNow, MfaEmailEnabled = state == "mfa" };
        user.PasswordHash = state == "google" ? null : new PasswordHasher<User>().HashPassword(user, OldPassword);
        if (state == "disabled") user.Status = UserStatus.Disabled;
        if (state == "pending") { user.Status = UserStatus.PendingVerification; user.EmailConfirmedAt = null; }
        db.Users.Add(user); db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.UserId }); await db.SaveChangesAsync(); return user;
    }
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key) => root.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
            ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
            ["Auth:Jwt:ClockSkewSeconds"] = "0", ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
            ["Auth:PasswordRecovery:Enabled"] = "true", ["Auth:PasswordRecovery:ResetUrl"] = "https://client.example.test/reset",
            ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test", ["Auth:Email:Sender"] = "sender@example.test",
            ["Auth:Email:Username"] = "test-user", ["Auth:Email:Password"] = "test-only-password",
            ["BruteForce:RateLimit:Policies:Recovery:IpPermitLimit"] = "100", ["BruteForce:RateLimit:Policies:Recovery:AccountPermitLimit"] = "100",
            ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "100"
        }));
        builder.ConfigureServices(services =>
        {
            // No transport is resolved or actual SMTP sent in this HTTP host.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PasswordEmailWorker)).ToArray()) services.Remove(d);
        });
    });
    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost"); return client;
    }
    private static async Task<AuthResult> Login(HttpClient client, User user, string password = OldPassword)
    {
        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<AuthResult>())!;
    }
    private static async Task<string> Token(WebApplicationFactory<Program> host, User user)
    {
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var c = await db.Challenges.AsNoTracking().Where(c => c.UserId == user.Id && c.Purpose == ChallengePurpose.ResetPassword).OrderByDescending(c => c.CreatedAt).FirstAsync();
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventId == c.Id && m.Kind == OutboxKind.PasswordEmail);
        return scope.ServiceProvider.GetRequiredService<IPasswordEmailProtector>().Unprotect(row.ProtectedPayload).Token!;
    }
    [Fact]
    public async Task Forgot_Generic202ForEveryAccountState_AndNoGooglePasswordProvisioning()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); await using var host = Host(root, key); using var client = Client(host);
        string? expected = null;
        foreach (var state in new[] { "active", "unknown", "google", "pending", "disabled" })
        {
            var user = await Seed(state);
            using var response = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new EmailRequest(state == "unknown" ? Guid.NewGuid() + "@example.test" : user.Email));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
            var body = await response.Content.ReadAsStringAsync(); expected ??= body; Assert.Equal(expected, body);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            await using var db = Db(); Assert.Equal(state == "active", await db.Challenges.AnyAsync(c => c.UserId == user.Id));
        }
    }
    [Fact]
    public async Task Reset_RevokesAccessAndRefreshAndRequiresNewPasswordWithoutAutoLogin()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); var user = await Seed();
        await using var host = Host(root, key); using var client = Client(host); var login = await Login(client, user);
        using var forgot = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new EmailRequest(user.Email)); Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        var token = await Token(host, user);
        using var reset = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new ResetPasswordRequest(token, NewPassword)); Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal("", await reset.Content.ReadAsStringAsync()); Assert.Contains("expires=", reset.Headers.GetValues("Set-Cookie").Single());
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        using var me = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(me, 401, "AUTH_UNAUTHENTICATED");
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null); await ErrorContractTests.AssertProblem(refresh, 401, "AUTH_REFRESH_INVALID");
        using var replay = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new ResetPasswordRequest(token, OldPassword)); await ErrorContractTests.AssertProblem(replay, 400, "AUTH_CHALLENGE_INVALID");
        client.DefaultRequestHeaders.Authorization = null;
        using var oldLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, OldPassword)); await ErrorContractTests.AssertProblem(oldLogin, 401, "AUTH_INVALID_CREDENTIALS");
        var fresh = await Login(client, user, NewPassword); Assert.Equal(user.Id, fresh.User.Id);
    }
    [Fact]
    public async Task RefreshDoesNotRenewRecentProof_ReauthThenChangeRevokesAllOwnerSessionsOnly()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await Seed(); var other = await Seed(); await using var host = Host(root, key); using var client = Client(host); using var foreign = Client(host);
        var login = await Login(client, user); var otherLogin = await Login(foreign, other);
        root.Clock.Advance(TimeSpan.FromSeconds(200));
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null); Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = (await refresh.Content.ReadFromJsonAsync<AuthResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", renewed.AccessToken);
        root.Clock.Advance(TimeSpan.FromSeconds(101));
        using var tooOld = await client.PostAsJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest(OldPassword, NewPassword)); await ErrorContractTests.AssertProblem(tooOld, 403, "AUTH_STEP_UP_REQUIRED");
        using var badReauth = await client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new ReauthenticateRequest("wrong password")); await ErrorContractTests.AssertProblem(badReauth, 401, "AUTH_INVALID_CREDENTIALS");
        using var reauth = await client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new ReauthenticateRequest(OldPassword)); Assert.Equal(HttpStatusCode.NoContent, reauth.StatusCode);
        using var change = await client.PostAsJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest(OldPassword, NewPassword)); Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        using var me = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(me, 401, "AUTH_UNAUTHENTICATED");
        // Foreign access expired naturally at 300 seconds; obtain a fresh token from its unrevoked family.
        using var foreignRefresh = await foreign.PostAsync("/api/v1/auth/refresh", null); Assert.Equal(HttpStatusCode.OK, foreignRefresh.StatusCode);
        foreign.DefaultRequestHeaders.Authorization = new("Bearer", (await foreignRefresh.Content.ReadFromJsonAsync<AuthResult>())!.AccessToken);
        using var foreignMe = await foreign.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.OK, foreignMe.StatusCode);
    }
    [Theory]
    [InlineData("forgot", "{\"email\":\"invalid\"}", "VALIDATION_FAILED")]
    [InlineData("reset", "{\"token\":\"bad\",\"newPassword\":\"short\"}", "VALIDATION_FAILED")]
    [InlineData("reset", "{\"token\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"newPassword\":\"a long secure password 123!\",\"userId\":\"other\"}", "INVALID_REQUEST")]
    public async Task RecoveryDtos_RejectInvalidInputsAndActorInjection(string route, string json, string code)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); await using var host = Host(root, key); using var client = Client(host);
        using var response = await client.PostAsync("/api/v1/auth/password/" + route, new StringContent(json, Encoding.UTF8, "application/json"));
        await ErrorContractTests.AssertProblem(response, 400, code);
    }
    [Fact]
    public async Task ChangeAndReauth_RequireCsrfWhenBrowserCookiesEnabled()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); var user = await Seed();
        await using var host = Host(root, key); using var client = Client(host); var login = await Login(client, user);
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken); client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var change = await client.PostAsJsonAsync("/api/v1/auth/password/change", new ChangePasswordRequest(OldPassword, NewPassword)); await ErrorContractTests.AssertProblem(change, 403, "AUTH_CSRF_INVALID");
        using var reauth = await client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new ReauthenticateRequest(OldPassword)); await ErrorContractTests.AssertProblem(reauth, 403, "AUTH_CSRF_INVALID");
        await using var db = Db(); Assert.Equal(1, (await db.Users.SingleAsync(u => u.Id == user.Id)).AuthVersion);
    }
    [Fact]
    public async Task ResetKeepsMfaEnabled_LoginCannotBypassSecondFactor()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); var user = await Seed("mfa");
        await using var host = Host(root, key); using var client = Client(host);
        using var forgot = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new EmailRequest(user.Email));
        using var reset = await client.PostAsJsonAsync("/api/v1/auth/password/reset", new ResetPasswordRequest(await Token(host, user), NewPassword)); Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var csrf = await client.GetAsync("/api/v1/auth/csrf"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, NewPassword)); await ErrorContractTests.AssertProblem(login, 403, "AUTH_STEP_UP_REQUIRED");
        await using var db = Db(); Assert.True((await db.Users.SingleAsync(u => u.Id == user.Id)).MfaEmailEnabled); Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
    }
}
