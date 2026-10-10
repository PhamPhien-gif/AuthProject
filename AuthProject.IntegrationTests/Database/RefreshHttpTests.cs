using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Security;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class RefreshHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string Password = "a long secure password 123!";
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task<User> Seed()
    {
        await using var db = Db();
        await db.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, Password);
        db.Users.Add(user); db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = SystemRoles.UserId });
        await db.SaveChangesAsync();
        return user;
    }
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key, int sessionQuota = 30) => root.WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
            ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:SigningKeyPath"] = key.Path, ["Auth:Jwt:KeyId"] = key.Settings.KeyId,
            ["Auth:Jwt:ClockSkewSeconds"] = "0", ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
            ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "100", ["BruteForce:RateLimit:Policies:Refresh:SessionPermitLimit"] = sessionQuota.ToString()
        })));
    private static HttpClient Client(WebApplicationFactory<Program> host) => host.CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    private static string Cookie(HttpResponseMessage response, string name) => response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(name + "=", StringComparison.Ordinal)).Split(';')[0];
    private sealed record Browser(string CsrfCookie, string Token);
    private static async Task<Browser> Csrf(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf"); request.Headers.Add("Origin", "https://localhost");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return new(Cookie(response, "__Host-AuthProject.Csrf"), (await response.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken);
    }
    private static HttpRequestMessage Request(Browser browser, string route, string? refresh = null, string? bearer = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/v1/auth/" + route);
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add(BrowserSessionCookies.CsrfHeader, browser.Token);
        request.Headers.Add("Cookie", browser.CsrfCookie + (refresh is null ? "" : "; " + refresh));
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return request;
    }
    private static async Task<(AuthResult Result, string Cookie)> Login(HttpClient client, Browser browser, User user)
    {
        using var request = Request(browser, "login"); request.Content = JsonContent.Create(new LoginRequest(user.Email, Password));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Cookie(response, BrowserSessionCookies.Name);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith(BrowserSessionCookies.Name + "="));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cookie.Split('=')[1], await response.Content.ReadAsStringAsync());
        return ((await response.Content.ReadFromJsonAsync<AuthResult>())!, cookie);
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [Fact]
    public async Task RefreshWorksWithExpiredAccess_ThenLogoutImmediatelyInvalidatesNewAccess()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var user = await Seed(); var browser = await Csrf(client); var login = await Login(client, browser, user);
        root.Clock.Advance(TimeSpan.FromMinutes(5));
        using var refresh = Request(browser, "refresh", login.Cookie, login.Result.AccessToken);
        using var response = await client.SendAsync(refresh);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<AuthResult>())!;
        var cookie = Cookie(response, BrowserSessionCookies.Name);
        Assert.NotEqual(login.Cookie, cookie);
        Assert.Equal(new JwtSecurityToken(login.Result.AccessToken).Claims.Single(x => x.Type == "sid").Value,
            new JwtSecurityToken(result.AccessToken).Claims.Single(x => x.Type == "sid").Value);
        using var logout = Request(browser, "logout", cookie); using var loggedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        var cleared = loggedOut.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("path=/api/v1/auth", cleared);
        Assert.Contains("samesite=strict", cleared);
        Assert.Contains("httponly", cleared); Assert.Contains("secure", cleared);
        using var meRequest = Request(browser, "me", bearer: result.AccessToken, method: HttpMethod.Get);
        using var me = await client.SendAsync(meRequest); await Error(me, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
        using var repeat = Request(browser, "logout", cookie); using var repeated = await client.SendAsync(repeat);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
    }
    [Theory]
    [InlineData("missing-header")]
    [InlineData("missing-cookie")]
    [InlineData("wrong-token")]
    [InlineData("other-browser")]
    [InlineData("evil-origin")]
    [InlineData("missing-origin")]
    [InlineData("null-origin")]
    public async Task CsrfFailure_CannotConsumeOrRevokeRefresh(string state)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var user = await Seed(); var browser = await Csrf(client); var login = await Login(client, browser, user);
        using var request = Request(browser, "refresh", login.Cookie);
        if (state is "missing-header" or "wrong-token" or "other-browser") request.Headers.Remove(BrowserSessionCookies.CsrfHeader);
        if (state == "wrong-token") request.Headers.Add(BrowserSessionCookies.CsrfHeader, "invalid");
        if (state == "other-browser") request.Headers.Add(BrowserSessionCookies.CsrfHeader, (await Csrf(client)).Token);
        if (state == "missing-cookie") { request.Headers.Remove("Cookie"); request.Headers.Add("Cookie", login.Cookie); }
        if (state is "evil-origin" or "missing-origin" or "null-origin") request.Headers.Remove("Origin");
        if (state == "evil-origin") request.Headers.Add("Origin", "https://evil.example.test");
        if (state == "null-origin") request.Headers.Add("Origin", "null");
        using var response = await client.SendAsync(request); await Error(response, HttpStatusCode.Forbidden, "AUTH_CSRF_INVALID");
        await using var db = Db();
        var session = await db.Sessions.SingleAsync(x => x.UserId == user.Id);
        Assert.Null(session.RevokedAt); Assert.Null((await db.RefreshTokens.SingleAsync(x => x.SessionId == session.Id)).ConsumedAt);
    }
    [Fact]
    public async Task LoginRequiresCsrf_AndCorsOnlyAllowsExactConfiguredOrigin()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var user = await Seed();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, Password));
        await Error(login, HttpStatusCode.Forbidden, "AUTH_CSRF_INVALID");
        foreach (var origin in new[] { "https://localhost", "https://localhost.evil.test" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
            request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", BrowserSessionCookies.CsrfHeader);
            using var response = await client.SendAsync(request);
            if (origin == "https://localhost") { Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single()); Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single()); }
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        await using var db = Db(); Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
    }
    [Fact]
    public async Task ReplayedCookie_ReturnsGeneric401AndRevokesSuccessorAccess()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var browser = await Csrf(client); var login = await Login(client, browser, await Seed());
        using var first = Request(browser, "refresh", login.Cookie); using var next = await client.SendAsync(first);
        var access = (await next.Content.ReadFromJsonAsync<AuthResult>())!.AccessToken;
        using var replay = Request(browser, "refresh", login.Cookie); using var response = await client.SendAsync(replay);
        await Error(response, HttpStatusCode.Unauthorized, "AUTH_REFRESH_INVALID");
        Assert.Contains("expires=", response.Headers.GetValues("Set-Cookie").Single());
        using var meRequest = Request(browser, "me", bearer: access, method: HttpMethod.Get); using var me = await client.SendAsync(meRequest);
        await Error(me, HttpStatusCode.Unauthorized, "AUTH_UNAUTHENTICATED");
    }
    [Fact]
    public async Task AnonymousRefreshSessionQuota_PreservesCookieAndHasRetryAfter()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key, sessionQuota: 1); using var client = Client(host);
        var browser = await Csrf(client); var login = await Login(client, browser, await Seed());
        using var first = Request(browser, "refresh", login.Cookie); using var next = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var cookie = Cookie(next, BrowserSessionCookies.Name);
        using var retry = Request(browser, "refresh", cookie); using var limited = await client.SendAsync(retry);
        await Error(limited, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.Equal(60, limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.False(limited.Headers.Contains("Set-Cookie"));
        root.Clock.Advance(TimeSpan.FromSeconds(60));
        using var after = Request(browser, "refresh", cookie); using var allowed = await client.SendAsync(after);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }
    [Fact]
    public async Task OwnerEndpoints_EnforceActorPaginationAndLogoutAllScope()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var browser = await Csrf(client); var owner = await Seed(); var first = await Login(client, browser, owner);
        var second = await Login(client, browser, owner); var foreign = await Login(client, browser, await Seed());
        var foreignId = new JwtSecurityToken(foreign.Result.AccessToken).Claims.Single(x => x.Type == "sid").Value;
        using var listRequest = Request(browser, "sessions?pageSize=1&userId=" + foreign.Result.User.Id, bearer: first.Result.AccessToken, method: HttpMethod.Get);
        using var list = await client.SendAsync(listRequest);
        var page = (await list.Content.ReadFromJsonAsync<PagedResponse<SessionResponse>>())!;
        Assert.Equal(2, page.TotalCount); Assert.Single(page.Items); Assert.DoesNotContain(page.Items, s => s.Id.ToString() == foreignId);
        using var badPaging = Request(browser, "sessions?pageSize=101", bearer: first.Result.AccessToken, method: HttpMethod.Get);
        using var badResponse = await client.SendAsync(badPaging); await Error(badResponse, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        using var idor = Request(browser, "sessions/" + foreignId, bearer: first.Result.AccessToken, method: HttpMethod.Delete);
        using var denied = await client.SendAsync(idor); await Error(denied, HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
        using var emptyId = Request(browser, "sessions/" + Guid.Empty, bearer: first.Result.AccessToken, method: HttpMethod.Delete);
        using var emptyResponse = await client.SendAsync(emptyId); await Error(emptyResponse, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var secondId = new JwtSecurityToken(second.Result.AccessToken).Claims.Single(x => x.Type == "sid").Value;
        using var owned = Request(browser, "sessions/" + secondId, bearer: first.Result.AccessToken, method: HttpMethod.Delete);
        using var revoked = await client.SendAsync(owned); Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var repeat = Request(browser, "sessions/" + secondId, bearer: first.Result.AccessToken, method: HttpMethod.Delete);
        using var repeated = await client.SendAsync(repeat); Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        using var all = Request(browser, "logout-all", first.Cookie, first.Result.AccessToken); using var logoutAll = await client.SendAsync(all);
        Assert.Equal(HttpStatusCode.NoContent, logoutAll.StatusCode);
        foreach (var token in new[] { first.Result.AccessToken, second.Result.AccessToken, foreign.Result.AccessToken })
        {
            using var meRequest = Request(browser, "me", bearer: token, method: HttpMethod.Get); using var me = await client.SendAsync(meRequest);
            Assert.Equal(token == foreign.Result.AccessToken ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, me.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed")]
    public async Task InvalidCookie_ReturnsGenericRefreshErrorButLogoutRemainsIdempotent(string? value)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = Client(host);
        var browser = await Csrf(client);
        var cookie = value is null ? null : BrowserSessionCookies.Name + "=" + value;
        using var refresh = Request(browser, "refresh", cookie); using var invalid = await client.SendAsync(refresh);
        await Error(invalid, HttpStatusCode.Unauthorized, "AUTH_REFRESH_INVALID");
        Assert.Contains("expires=", invalid.Headers.GetValues("Set-Cookie").Single());
        using var logout = Request(browser, "logout", cookie); using var response = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
