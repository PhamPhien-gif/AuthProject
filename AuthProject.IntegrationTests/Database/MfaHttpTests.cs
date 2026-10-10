using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using AuthProject.BackgroundJobs;
using AuthProject.Data;
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
public sealed class MfaHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string Password = "a long secure password 123!";
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task<User> Seed(bool mfa = true)
    {
        await using var db = Db(); await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active,
            EmailConfirmedAt = DateTimeOffset.UtcNow, MfaEmailEnabled = mfa };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, Password); db.Users.Add(user);
        await db.SaveChangesAsync(); return user;
    }
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key, int otpQuota = 100) => root.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
            ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
            ["Auth:Jwt:ClockSkewSeconds"] = "0", ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
            ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test", ["Auth:Email:Sender"] = "sender@example.test",
            ["Auth:Email:Username"] = "test", ["Auth:Email:Password"] = "test-only-password",
            ["Auth:EmailOtp:Enabled"] = "true", ["Auth:EmailOtp:KeyId"] = "test", ["Auth:EmailOtp:Pepper"] = Convert.ToBase64String(new byte[32]),
            ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "100", ["BruteForce:RateLimit:Policies:Login:AccountPermitLimit"] = "100"
            , ["BruteForce:RateLimit:Policies:Otp:IpPermitLimit"] = otpQuota.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        builder.ConfigureServices(services =>
        {
            // All email delivery stays inside the isolated dispatcher tests.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MfaEmailWorker)).ToArray()) services.Remove(d);
        });
    });
    private static async Task<HttpClient> Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken);
        return client;
    }
    private static async Task<MfaEmail> Mail(WebApplicationFactory<Program> host, Guid userId)
    {
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await (from m in db.OutboxMessages.AsNoTracking() join a in db.AuditEvents on m.EventId equals a.Id
            where m.Kind == OutboxKind.MfaEmail && a.SubjectId == userId orderby m.CreatedAt descending select m).FirstAsync();
        return scope.ServiceProvider.GetRequiredService<MfaEmailProtector>().Unprotect(row.ProtectedPayload);
    }
    [Fact]
    public async Task Login_does_not_authenticate_before_MFA_and_completes_with_refresh_and_current_Me()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var user = await Seed();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, Password));
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode); Assert.True(login.Headers.CacheControl!.NoStore);
        var body = await login.Content.ReadAsStringAsync(); Assert.DoesNotContain("accessToken", body); Assert.DoesNotContain("refreshToken", body);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(); Assert.Contains("__Secure-AuthProject.Mfa=", cookie);
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
        using var me = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(me, 401, "AUTH_UNAUTHENTICATED");
        using var refreshPending = await client.PostAsync("/api/v1/auth/refresh", null); await ErrorContractTests.AssertProblem(refreshPending, 401, "AUTH_REFRESH_INVALID");
        var challenge = (await login.Content.ReadFromJsonAsync<MfaChallengeResponse>())!; var mail = await Mail(host, user.Id);
        using var missingBrowser = await Client(host);
        using var stolen = await missingBrowser.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(challenge.ChallengeId, mail.Code));
        await ErrorContractTests.AssertProblem(stolen, 400, "AUTH_CHALLENGE_INVALID");
        using var verified = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(challenge.ChallengeId, mail.Code));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode); var result = (await verified.Content.ReadFromJsonAsync<AuthResult>())!;
        Assert.DoesNotContain("refreshToken", await verified.Content.ReadAsStringAsync());
        Assert.Contains(verified.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Secure-AuthProject.Refresh="));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken); Assert.Contains(jwt.Claims, c => c.Type == "amr" && c.Value == "mfa");
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        using var current = await client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.True((await current.Content.ReadFromJsonAsync<MeResponse>())!.MfaEnabled);
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null); Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Contains(new JwtSecurityTokenHandler().ReadJwtToken((await refresh.Content.ReadFromJsonAsync<AuthResult>())!.AccessToken).Claims,
            c => c.Type == "amr" && c.Value == "mfa");
    }
    [Fact]
    public async Task Enrollment_revokes_old_bearer_and_returns_recovery_only_once()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var user = await Seed(false);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); client.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<AuthResult>())!.AccessToken);
        using var enroll = await client.PostAsync("/api/v1/auth/mfa/email/enroll", null); Assert.Equal(HttpStatusCode.Accepted, enroll.StatusCode);
        var challenge = (await enroll.Content.ReadFromJsonAsync<MfaChallengeResponse>())!; var mail = await Mail(host, user.Id);
        using var wrongPurpose = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(challenge.ChallengeId, mail.Code));
        await ErrorContractTests.AssertProblem(wrongPurpose, 400, "AUTH_CHALLENGE_INVALID");
        using var enable = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/enable", new VerifyOtpRequest(challenge.ChallengeId, mail.Code));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode); Assert.True(enable.Headers.CacheControl!.NoStore);
        var codes = (await enable.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.RecoveryCodes; Assert.Equal(10, codes.Length);
        using var revoked = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(revoked, 401, "AUTH_UNAUTHENTICATED");
        client.DefaultRequestHeaders.Authorization = null; root.Clock.Advance(TimeSpan.FromSeconds(600));
        using var again = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, Password)); Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var next = (await again.Content.ReadFromJsonAsync<MfaChallengeResponse>())!;
        using var recovery = await client.PostAsJsonAsync("/api/v1/auth/mfa/recovery/verify", new RecoveryCodeRequest(next.ChallengeId, codes[0]));
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);
    }
    [Theory]
    [InlineData("email/verify")] [InlineData("email/resend")] [InlineData("recovery/verify")]
    public async Task Cookie_operations_require_CSRF_and_allowed_origin(string path)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var missing = await client.PostAsJsonAsync("/api/v1/auth/mfa/" + path, new { challengeId = Guid.NewGuid() });
        await ErrorContractTests.AssertProblem(missing, 403, "AUTH_CSRF_INVALID");
    }
    [Theory]
    [InlineData("12345")] [InlineData("１２３４５６")] [InlineData("123456 ")]
    public async Task Verify_validates_ASCII_six_digit_DTO(string code)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(Guid.NewGuid(), code));
        await ErrorContractTests.AssertProblem(response, 400, "VALIDATION_FAILED");
    }
    [Fact]
    public async Task OTP_policy_limits_IP_requests_before_challenge_lookup()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key, otpQuota: 1); using var client = await Client(host);
        using var first = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(Guid.NewGuid(), "123456"));
        await ErrorContractTests.AssertProblem(first, 400, "AUTH_CHALLENGE_INVALID");
        using var limited = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(Guid.NewGuid(), "123456"));
        await ErrorContractTests.AssertProblem(limited, 429, "RATE_LIMITED"); Assert.NotNull(limited.Headers.RetryAfter);
    }
}
