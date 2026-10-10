using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Foundation;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Security;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Interfaces;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class LoginHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string Password = "a long secure password 123!";
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task<User> SeedAsync(AuthWebApplicationFactory root, string state = "active", bool legacy = false)
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = root.Clock.GetUtcNow(), MfaEmailEnabled = state == "mfa" };
        var hash = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = legacy ? 10000 : 210000 }));
        user.PasswordHash = state == "google" ? null : hash.HashPassword(user, Password);
        if (state == "pending") { user.Status = UserStatus.PendingVerification; user.EmailConfirmedAt = null; }
        if (state == "disabled") user.Status = UserStatus.Disabled;
        if (state == "locked") user.LockoutEnd = root.Clock.GetUtcNow().AddMinutes(15);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.UserId });
        await db.SaveChangesAsync();
        return user;
    }
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key) => root.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
            ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:SigningKeyPath"] = key.Path, ["Auth:Jwt:KeyId"] = key.Settings.KeyId,
            ["Auth:Jwt:ClockSkewSeconds"] = "0", ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "100"
        }));
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(AuthorizationProbeController).Assembly));
    });
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static async Task<AuthResult> Login(HttpClient client, User user)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        return (await response.Content.ReadFromJsonAsync<AuthResult>())!;
    }
    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task LoginAndGetMe_ReturnOnlyOwnProfileAndInheritAuthenticationAndRoles()
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root);
        await using var host = Host(root, key);
        using var client = Client(host);
        using var anonymous = await client.GetAsync("/api/v1/auth/me");
        await AssertError(anonymous, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
        Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        var result = await Login(client, user);
        Assert.Equal(300, result.ExpiresIn);
        Assert.Equal(user.Id, result.User.Id);
        Assert.Equal(new[] { "User" }, result.User.Roles);
        var jwt = new JwtSecurityToken(result.AccessToken);
        Assert.Equal("RS256", jwt.Header.Alg);
        Assert.Equal("at+jwt", jwt.Header.Typ);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        using var me = await client.GetAsync("/api/v1/auth/me?userId=" + Guid.NewGuid());
        var payload = await me.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authVersion", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(user.Id, (await me.Content.ReadFromJsonAsync<MeResponse>())!.Id);
        using var ordinary = await client.GetAsync("/_tests/p06/default");
        Assert.Equal(HttpStatusCode.NoContent, ordinary.StatusCode);
        using var adminDenied = await client.GetAsync("/_tests/p06/admin");
        await AssertError(adminDenied, HttpStatusCode.Forbidden, "AUTH_FORBIDDEN");
        await using var db = Db();
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.AdminId });
        await db.SaveChangesAsync();
        using var adminAllowed = await client.GetAsync("/_tests/p06/admin");
        Assert.Equal(HttpStatusCode.NoContent, adminAllowed.StatusCode);
        db.UserRoles.Remove(await db.UserRoles.SingleAsync(x => x.UserId == user.Id && x.RoleId == SystemRoles.AdminId));
        await db.SaveChangesAsync();
        using var adminRemoved = await client.GetAsync("/_tests/p06/admin");
        await AssertError(adminRemoved, HttpStatusCode.Forbidden, "AUTH_FORBIDDEN");
        Assert.Single(await db.Sessions.Where(x => x.UserId == user.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("unknown")]
    [InlineData("pending")]
    [InlineData("disabled")]
    [InlineData("locked")]
    [InlineData("google")]
    public async Task Login_FailureStatesUseSamePublic401AndCommitFailureHistory(string state)
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root, state);
        await using var host = Host(root, key);
        using var client = Client(host);
        var email = state == "unknown" ? Guid.NewGuid() + "@example.test" : user.Email;
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, state == "wrong" ? "wrong password" : Password));
        await AssertError(response, HttpStatusCode.Unauthorized, "AUTH_INVALID_CREDENTIALS");
        await using var db = Db();
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == user.Id));
        var trace = response.Headers.GetValues("X-Trace-Id").Single();
        var history = await db.LoginHistories.SingleAsync(x => x.TraceId == trace);
        Assert.Equal(AuditOutcome.Failure, history.Outcome);
        Assert.Equal(state == "unknown" ? null : (Guid?)user.Id, history.UserId);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("version")]
    [InlineData("session-version")]
    [InlineData("idle")]
    [InlineData("absolute")]
    [InlineData("jwt-expired")]
    [InlineData("mfa")]
    public async Task GetMe_RechecksSessionAccountAndExpiryAfterIssuance(string state)
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root);
        await using var host = Host(root, key);
        using var client = Client(host);
        var result = await Login(client, user);
        await using var db = Db();
        var session = await db.Sessions.SingleAsync(x => x.UserId == user.Id);
        var current = await db.Users.SingleAsync(x => x.Id == user.Id);
        if (state == "revoked") session.RevokedAt = root.Clock.GetUtcNow();
        if (state == "disabled") current.Status = UserStatus.Disabled;
        if (state == "version") current.AuthVersion++;
        if (state == "session-version") session.AuthVersion++;
        if (state == "mfa") current.MfaEmailEnabled = true;
        if (state == "idle") { session.IdleExpiresAt = session.CreatedAt.AddSeconds(1); root.Clock.Advance(TimeSpan.FromSeconds(1)); }
        if (state == "absolute") { session.IdleExpiresAt = session.AbsoluteExpiresAt = session.CreatedAt.AddSeconds(1); root.Clock.Advance(TimeSpan.FromSeconds(1)); }
        if (state == "jwt-expired") root.Clock.Advance(TimeSpan.FromSeconds(300));
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        using var response = await client.GetAsync("/api/v1/auth/me");
        await AssertError(response, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
    }

    private static string MutateToken(AuthResult result, TemporaryJwtKey key, string change, DateTimeOffset now)
    {
        var original = new JwtSecurityToken(result.AccessToken);
        var claims = original.Claims.Where(x => x.Type is not "iss" and not "aud" and not "exp" and not "nbf").ToList();
        if (change.StartsWith("missing-")) claims.RemoveAll(x => x.Type == change[8..]);
        if (change == "wrong-sub") { claims.RemoveAll(x => x.Type == "sub"); claims.Add(new Claim("sub", Guid.NewGuid().ToString())); }
        if (change == "duplicate-sid") claims.Add(new Claim("sid", Guid.NewGuid().ToString()));
        if (change == "role") claims.Add(new Claim("role", "Admin"));
        if (change == "future-iat") { claims.RemoveAll(x => x.Type == "iat"); claims.Add(new Claim("iat", now.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))); }
        using var other = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(change == "signature" ? other : key.Rsa) { KeyId = change == "kid" ? "wrong-key" : key.Settings.KeyId };
        SigningCredentials? credentials = change == "unsigned" ? null : new SigningCredentials(signingKey, change == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256);
        if (change == "hs256") credentials = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = key.Settings.KeyId }, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(change == "issuer" ? "https://wrong.example.test" : key.Settings.Issuer,
            change == "audience" ? "wrong-api" : change == "audience-slash" ? key.Settings.Audience + "/" : key.Settings.Audience, claims,
            change == "nbf" ? now.AddSeconds(60).UtcDateTime : now.UtcDateTime, now.AddMinutes(5).UtcDateTime, credentials);
        token.Header["typ"] = change == "type" ? "JWT" : "at+jwt";
        if (change == "missing-exp") token.Payload.Remove("exp");
        if (change == "missing-nbf") token.Payload.Remove("nbf");
        if (change == "missing-kid") token.Header.Remove("kid");
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("algorithm")]
    [InlineData("unsigned")]
    [InlineData("kid")]
    [InlineData("nbf")]
    [InlineData("type")]
    [InlineData("missing-sub")]
    [InlineData("missing-sid")]
    [InlineData("missing-av")]
    [InlineData("missing-jti")]
    [InlineData("missing-iat")]
    [InlineData("missing-amr")]
    [InlineData("wrong-sub")]
    [InlineData("duplicate-sid")]
    [InlineData("missing-exp")]
    [InlineData("missing-nbf")]
    [InlineData("future-iat")]
    [InlineData("hs256")]
    [InlineData("missing-kid")]
    [InlineData("audience-slash")]
    public async Task JwtBearer_RejectsForgedOrInvalidTokenWithoutLeakingProtocolDetails(string change)
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root);
        await using var host = Host(root, key);
        using var client = Client(host);
        var result = await Login(client, user);
        var invalid = MutateToken(result, key, change, root.Clock.GetUtcNow());
        client.DefaultRequestHeaders.Authorization = new("Bearer", invalid);
        using var response = await client.GetAsync("/api/v1/auth/me");
        await AssertError(response, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
        Assert.DoesNotContain(invalid, await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task MfaAccount_CannotBypassFactorAndLegacyPasswordIsRehashedAtomically()
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var mfa = await SeedAsync(root, "mfa");
        var legacy = await SeedAsync(root, legacy: true);
        await using var host = Host(root, key);
        using var client = Client(host);
        using var denied = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(mfa.Email, Password));
        await AssertError(denied, HttpStatusCode.Forbidden, "AUTH_STEP_UP_REQUIRED");
        var result = await Login(client, legacy);
        await using var db = Db();
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == mfa.Id));
        Assert.Single(await db.LoginHistories.Where(x => x.UserId == mfa.Id && x.Outcome == AuditOutcome.Pending).ToArrayAsync());
        var rehashed = await db.Users.AsNoTracking().SingleAsync(x => x.Id == legacy.Id);
        Assert.NotEqual(legacy.PasswordHash, rehashed.PasswordHash);
        var hashes = new PasswordHashService(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
        Assert.Equal(AuthProject.Security.Interfaces.PasswordCheck.Success, hashes.Verify(rehashed, rehashed.PasswordHash!, Password));
        Assert.Equal(legacy.Id, result.User.Id);
    }

    [Fact]
    public async Task Bearer_UsesOnlyAuthorizationHeaderAndCurrentSqlRoles()
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root);
        await using var host = Host(root, key);
        using var client = Client(host);
        var result = await Login(client, user);
        using var query = await client.GetAsync("/api/v1/auth/me?access_token=" + result.AccessToken);
        await AssertError(query, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
        client.DefaultRequestHeaders.Authorization = new("Bearer", MutateToken(result, key, "role", root.Clock.GetUtcNow()));
        using var role = await client.GetAsync("/_tests/p06/admin");
        await AssertError(role, HttpStatusCode.Forbidden, "AUTH_FORBIDDEN");
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('A', 8193));
        using var oversized = await client.GetAsync("/api/v1/auth/me");
        await AssertError(oversized, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
    }

    private sealed class UnavailableDatabase : System.Data.Common.DbException
    {
        public UnavailableDatabase() : base("DO_NOT_LEAK_SQL_CREDENTIALS") { }
    }
    private sealed class UnavailableSessionStore : ILoginStore
    {
        public Task<AuthResult?> CompleteAsync(PasswordAcceptance accepted, string password, string traceId, string? ip, string? userAgent, CancellationToken token) => throw new NotSupportedException();
        public Task<MeResponse?> GetMeAsync(Guid user, CancellationToken token) => throw new NotSupportedException();
        public Task<string[]?> ValidateSessionAsync(Guid user, Guid session, long version, CancellationToken token) => throw new UnavailableDatabase();
    }
    [Fact]
    public async Task SessionDatabaseFailure_FailsClosedWithSafe503()
    {
        using var key = new TemporaryJwtKey();
        await using var root = new AuthWebApplicationFactory(database: fixture);
        var user = await SeedAsync(root);
        await using var host = Host(root, key);
        using var client = Client(host);
        var result = await Login(client, user);
        await using var unavailable = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILoginStore>();
            services.AddScoped<ILoginStore, UnavailableSessionStore>();
        }));
        using var unavailableClient = Client(unavailable);
        unavailableClient.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        using var response = await unavailableClient.GetAsync("/api/v1/auth/me");
        await AssertError(response, HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE");
        Assert.DoesNotContain("DO_NOT_LEAK", await response.Content.ReadAsStringAsync());
    }
}
