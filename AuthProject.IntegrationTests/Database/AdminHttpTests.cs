using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Foundation;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Security;
using AuthProject.Security.Authentication;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Email;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AdminHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private AuthDbContext Db(TimeProvider? clock = null) => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options, clock);
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key) => root.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
            ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
            ["Auth:Jwt:ClockSkewSeconds"] = "0", ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost"
        }));
    });
    private static async Task<HttpClient> Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var csrf = await client.GetAsync("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken); return client;
    }
    private async Task<(User User, AuthSession Session, string Token, string Refresh)> Seed(WebApplicationFactory<Program> host, TimeProvider clock, string state = "admin")
    {
        await using var db = Db(clock); await db.Database.MigrateAsync(); var now = clock.GetUtcNow();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Account", Status = UserStatus.Active,
            EmailConfirmedAt = now, MfaEmailEnabled = state != "enroll" };
        var session = new AuthSession { UserId = user.Id, AuthVersion = user.AuthVersion, CreatedAt = now, LastSeenAt = now,
            AuthenticatedAt = state == "old" ? now.AddSeconds(-300) : now, MfaVerifiedAt = state == "enroll" ? null : state == "old" ? now.AddSeconds(-300) : now,
            IdleExpiresAt = now.AddDays(1), AbsoluteExpiresAt = now.AddDays(2) };
        db.Users.Add(user); db.Sessions.Add(session); db.UserRoles.Add(new() { UserId = user.Id, RoleId = state == "user" ? SystemRoles.UserId : SystemRoles.AdminId });
        var refresh = RefreshTokenSecret.Create(); db.RefreshTokens.Add(new() { SessionId = session.Id, TokenHash = RefreshTokenSecret.Digest(refresh)!, CreatedAt = now, ExpiresAt = session.IdleExpiresAt });
        await db.SaveChangesAsync(); await using var scope = host.Services.CreateAsyncScope();
        var token = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>().Issue(session); return (user, session, token, refresh);
    }
    [Theory]
    [InlineData("anonymous", 401, "AUTH_UNAUTHENTICATED")]
    [InlineData("user", 403, "AUTH_FORBIDDEN")]
    [InlineData("enroll", 403, "AUTH_MFA_REQUIRED")]
    [InlineData("admin", 200, null)]
    [InlineData("old", 200, null)]
    public async Task Admin_reads_require_current_SQL_role_and_MFA(string state, int status, string? error)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host);
        if (state != "anonymous") { var account = await Seed(host, root.Clock, state); client.DefaultRequestHeaders.Authorization = new("Bearer", account.Token); }
        using var response = await client.GetAsync("/api/v1/admin/roles");
        if (error is not null) await ErrorContractTests.AssertProblem(response, status, error);
        else { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(2, (await response.Content.ReadFromJsonAsync<RoleResponse[]>())!.Length); }
    }
    [Fact]
    public async Task Admin_mutations_revoke_old_bearer_and_refresh_and_show_current_roles()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var actorClient = await Client(host); using var targetClient = await Client(host);
        var actor = await Seed(host, root.Clock); var target = await Seed(host, root.Clock, "user");
        actorClient.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token); targetClient.DefaultRequestHeaders.Authorization = new("Bearer", target.Token);
        using var changed = await actorClient.PutAsJsonAsync($"/api/v1/admin/users/{target.User.Id}/roles", new SetUserRolesRequest(["User", "Admin"]));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        using var oldMe = await targetClient.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(oldMe, 401, "AUTH_UNAUTHENTICATED");
        targetClient.DefaultRequestHeaders.Authorization = null;
        targetClient.DefaultRequestHeaders.Add("Cookie", BrowserSessionCookies.Name + "=" + target.Refresh);
        using var oldRefresh = await targetClient.PostAsync("/api/v1/auth/refresh", null); await ErrorContractTests.AssertProblem(oldRefresh, 401, "AUTH_REFRESH_INVALID");
        using var list = await actorClient.GetAsync("/api/v1/admin/users?search=" + target.User.Email + "&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode); var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("authVersion", body, StringComparison.OrdinalIgnoreCase);
        var page = (await list.Content.ReadFromJsonAsync<PagedResponse<AdminUserResponse>>())!; Assert.Equal(1, page.TotalCount);
        Assert.Equal(new[] { "Admin", "User" }, page.Items.Single().Roles);
        using var disabled = await actorClient.PatchAsJsonAsync($"/api/v1/admin/users/{target.User.Id}/status", new ChangeUserStatusRequest("Disabled")); Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        await using var db = Db(); Assert.Equal(UserStatus.Disabled, (await db.Users.SingleAsync(u => u.Id == target.User.Id)).Status);
    }
    [Theory]
    [InlineData("old", 403, "AUTH_STEP_UP_REQUIRED")]
    [InlineData("csrf", 403, "AUTH_CSRF_INVALID")]
    [InlineData("unknown", 404, "RESOURCE_NOT_FOUND")]
    [InlineData("pending", 409, "RESOURCE_CONFLICT")]
    public async Task Admin_writes_recheck_proof_csrf_and_target(string state, int status, string code)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var actor = await Seed(host, root.Clock, state == "old" ? "old" : "admin");
        var target = await Seed(host, root.Clock, "user"); client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token);
        if (state == "csrf") client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (state == "pending") { await using var db = Db(); await db.Users.Where(u => u.Id == target.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.PendingVerification).SetProperty(u => u.EmailConfirmedAt, (DateTimeOffset?)null)); }
        using var response = await client.PatchAsJsonAsync($"/api/v1/admin/users/{(state == "unknown" ? Guid.NewGuid() : target.User.Id)}/status", new ChangeUserStatusRequest("Active"));
        await ErrorContractTests.AssertProblem(response, status, code);
    }
    [Theory]
    [InlineData("roles")] [InlineData("status")] [InlineData("query")] [InlineData("id")]
    public async Task Invalid_admin_DTOs_and_mass_assignment_are_rejected(string state)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var actor = await Seed(host, root.Clock);
        client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token);
        using var response = state switch
        {
            "roles" => await client.PutAsJsonAsync($"/api/v1/admin/users/{Guid.NewGuid()}/roles", new { roleNames = new[] { "Admin" }, authVersion = 0 }),
            "status" => await client.PatchAsJsonAsync($"/api/v1/admin/users/{Guid.NewGuid()}/status", new ChangeUserStatusRequest("PendingVerification")),
            "query" => await client.GetAsync("/api/v1/admin/users?page=0&pageSize=101&status=unsafe"),
            _ => await client.PutAsJsonAsync("/api/v1/admin/users/invalid/roles", new SetUserRolesRequest(["User"]))
        };
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
    [Fact]
    public async Task Profile_changes_only_actor_display_name_and_rejects_role_injection()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var actor = await Seed(host, root.Clock, "user"); var other = await Seed(host, root.Clock, "user");
        client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token);
        using var injected = await client.PatchAsJsonAsync("/api/v1/users/me", new { displayName = "Changed", userId = other.User.Id, roleNames = new[] { "Admin" } });
        await ErrorContractTests.AssertProblem(injected, 400, "INVALID_REQUEST");
        using var changed = await client.PatchAsJsonAsync("/api/v1/users/me?userId=" + other.User.Id, new UpdateProfileRequest(" Changed ")); Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        await using var db = Db(); Assert.Equal("Changed", (await db.Users.SingleAsync(u => u.Id == actor.User.Id)).DisplayName);
        Assert.Equal("Account", (await db.Users.SingleAsync(u => u.Id == other.User.Id)).DisplayName);
        Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == actor.User.Id && r.RoleId == SystemRoles.AdminId));
    }
    [Fact]
    public async Task Role_revocation_in_SQL_denies_admin_even_if_old_bearer_is_signed()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var actor = await Seed(host, root.Clock); client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token);
        await using (var db = Db()) await db.UserRoles.Where(r => r.UserId == actor.User.Id).ExecuteDeleteAsync();
        using var response = await client.GetAsync("/api/v1/admin/users"); await ErrorContractTests.AssertProblem(response, 403, "AUTH_FORBIDDEN");
    }
    [Theory]
    [InlineData("user", "AUTH_FORBIDDEN")] [InlineData("enroll", "AUTH_MFA_REQUIRED")]
    public async Task Signed_JWT_role_or_MFA_claim_cannot_replace_SQL_evidence(string state, string code)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var account = await Seed(host, root.Clock, state);
        var parsed = new JwtSecurityToken(account.Token);
        var claims = parsed.Claims.Where(c => c.Type is not ("iss" or "aud" or "nbf" or "exp" or "amr"))
            .Concat(new[] { new Claim("role", "Admin"), new Claim("amr", "pwd"), new Claim("amr", "mfa") });
        await using var scope = host.Services.CreateAsyncScope();
        var forged = new JwtSecurityToken(key.Settings.Issuer, key.Settings.Audience, claims, root.Clock.GetUtcNow().UtcDateTime,
            root.Clock.GetUtcNow().AddMinutes(5).UtcDateTime, scope.ServiceProvider.GetRequiredService<JwtSigningKey>().Credentials);
        forged.Header["typ"] = "at+jwt";
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(forged));
        using var response = await client.GetAsync("/api/v1/admin/users"); await ErrorContractTests.AssertProblem(response, 403, code);
    }
}
