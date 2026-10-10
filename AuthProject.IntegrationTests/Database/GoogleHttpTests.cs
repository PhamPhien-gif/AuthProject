using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using AuthProject.BackgroundJobs;
using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.IntegrationTests.Foundation;
using AuthProject.Services.Email;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class GoogleHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task Migrate() { await using var db = Db(); await db.Database.MigrateAsync(); }
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key)
    {
        root.Clock.SetUtcNow(DateTimeOffset.UtcNow);
        return root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
                ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
                ["Auth:Refresh:Enabled"] = "true", ["Auth:Refresh:AllowedOrigins:0"] = "https://localhost",
                ["Auth:Google:Enabled"] = "true", ["Auth:Google:ClientId"] = FakeGoogleBackchannel.ClientId, ["Auth:Google:ClientSecret"] = "fake-secret",
                ["Auth:Google:CallbackUri"] = "https://localhost/signin-google", ["Auth:Google:CompletionUrl"] = "https://localhost/google-complete",
                ["Auth:Google:ErrorUrl"] = "https://localhost/google-error",
                ["Auth:Email:Enabled"] = "true", ["Auth:Email:Host"] = "smtp.example.test", ["Auth:Email:Sender"] = "sender@example.test",
                ["Auth:Email:Username"] = "test", ["Auth:Email:Password"] = "test-only",
                ["Auth:EmailOtp:Enabled"] = "true", ["Auth:EmailOtp:KeyId"] = "test", ["Auth:EmailOtp:Pepper"] = Convert.ToBase64String(new byte[32]),
                ["BruteForce:RateLimit:Policies:Google:IpPermitLimit"] = "100", ["BruteForce:RateLimit:Policies:Otp:IpPermitLimit"] = "100"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MfaEmailWorker)).ToArray()) services.Remove(d);
                services.PostConfigure<OpenIdConnectOptions>(GoogleServices.Scheme, o =>
                {
                    o.Authority = FakeGoogleBackchannel.Issuer; o.Backchannel = new HttpClient(root.Google);
                    o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(FakeGoogleBackchannel.Issuer + "/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(o.Backchannel) { RequireHttps = true });
                    o.TokenValidationParameters.ValidIssuers = [FakeGoogleBackchannel.Issuer];
                });
            });
        });
    }
    private static async Task<HttpClient> Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var csrf = await client.GetAsync("/api/v1/auth/csrf"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await csrf.Content.ReadFromJsonAsync<CsrfResponse>())!.RequestToken);
        return client;
    }
    private static TestIdentity Identity(bool verified = true) => new TestIdentityBuilder().WithEmail(Guid.NewGuid() + "@example.test").Build() with { EmailVerified = verified };
    private static async Task<(Uri Authorization, string State, string Nonce, string Pkce)> Start(HttpClient client)
    {
        using var start = await client.GetAsync("/api/v1/auth/google/start?returnUrl=https://attacker.test"); Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var url = start.Headers.Location!; var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("code", query["response_type"]); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://localhost/signin-google", query["redirect_uri"]);
        Assert.Equal(new[] { "email", "openid", "profile" }, query["scope"].ToString().Split(' ').Order().ToArray());
        return (url, query["state"].ToString(), query["nonce"].ToString(), query["code_challenge"].ToString());
    }
    private static Task<HttpResponseMessage> Callback(HttpClient client, string code, string state) => client.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string, string?> { ["code"] = code, ["state"] = state }));
    private static string Ticket(HttpResponseMessage callback)
    {
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/google-complete", callback.Headers.Location!.AbsolutePath);
        Assert.DoesNotContain("accessToken", callback.Headers.Location.ToString()); Assert.DoesNotContain("refresh", callback.Headers.Location.ToString());
        return QueryHelpers.ParseQuery(callback.Headers.Location.Fragment.TrimStart('#'))["ticket"].ToString();
    }
    [Fact]
    public async Task Full_code_PKCE_flow_creates_account_and_issues_only_on_POST_completion()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var start = await Start(client);
        var code = root.Google.IssueCode(identity, start.Nonce, start.Pkce); using var callback = await Callback(client, code, start.State); var ticket = Ticket(callback);
        Assert.True(callback.Headers.CacheControl!.NoStore); Assert.DoesNotContain(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-AuthProject.External="));
        using var before = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(before, 401, "AUTH_UNAUTHENTICATED");
        using var completed = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(ticket)); Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var auth = (await completed.Content.ReadFromJsonAsync<AuthResult>())!;
        Assert.Contains(new JwtSecurityTokenHandler().ReadJwtToken(auth.AccessToken).Claims, c => c.Type == "amr" && c.Value == "federated");
        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken); using var me = await client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null); Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Contains(new JwtSecurityTokenHandler().ReadJwtToken((await refresh.Content.ReadFromJsonAsync<AuthResult>())!.AccessToken).Claims, c => c.Type == "amr" && c.Value == "federated");
        client.DefaultRequestHeaders.Authorization = null;
        using var replay = await Callback(client, code, start.State); Assert.Equal("/google-error", replay.Headers.Location!.AbsolutePath);
        await using var db = Db(); var user = await db.Users.SingleAsync(u => u.Email == identity.Email); Assert.Null(user.PasswordHash);
        Assert.Single(await db.ExternalLogins.Where(x => x.UserId == user.Id).ToArrayAsync()); Assert.Single(await db.Sessions.Where(s => s.UserId == user.Id).ToArrayAsync());
    }
    [Theory]
    [InlineData(GoogleTokenFault.WrongIssuer)] [InlineData(GoogleTokenFault.WrongAudience)] [InlineData(GoogleTokenFault.WrongSignature)]
    [InlineData(GoogleTokenFault.Expired)] [InlineData(GoogleTokenFault.MissingNonce)] [InlineData(GoogleTokenFault.MissingSubject)] [InlineData(GoogleTokenFault.Unavailable)]
    public async Task Invalid_provider_evidence_never_creates_account(GoogleTokenFault fault)
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var start = await Start(client);
        using var callback = await Callback(client, root.Google.IssueCode(identity, start.Nonce, start.Pkce, fault), start.State);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/google-error", callback.Headers.Location!.AbsolutePath);
        Assert.DoesNotContain(identity.Email, callback.Headers.Location.ToString()); await using var db = Db(); Assert.False(await db.Users.AnyAsync(u => u.Email == identity.Email));
    }
    [Theory]
    [InlineData("state")] [InlineData("nonce")] [InlineData("browser")] [InlineData("canceled")] [InlineData("email")]
    public async Task Wrong_state_nonce_browser_and_canceled_or_unverified_consent_are_rejected(string failure)
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); using var other = await Client(host);
        var identity = Identity(failure != "email"); var start = await Start(client);
        var code = root.Google.IssueCode(identity, failure == "nonce" ? "wrong-nonce" : start.Nonce, start.Pkce);
        using var callback = failure == "canceled" ? await client.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string,string?> { ["error"] = "access_denied", ["state"] = start.State }))
            : await Callback(failure == "browser" ? other : client, code, failure == "state" ? "tampered-state" : start.State);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode); Assert.Equal("/google-error", callback.Headers.Location!.AbsolutePath);
        await using var db = Db(); Assert.False(await db.Users.AnyAsync(u => u.Email == identity.Email));
    }
    [Fact]
    public async Task Completion_ticket_without_browser_cookie_is_rejected()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); using var other = await Client(host); var start = await Start(client);
        using var callback = await Callback(client, root.Google.IssueCode(Identity(), start.Nonce, start.Pkce), start.State);
        using var stolen = await other.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(Ticket(callback)));
        await ErrorContractTests.AssertProblem(stolen, 400, "AUTH_GOOGLE_INVALID");
    }
    [Fact]
    public async Task Google_login_cannot_bypass_local_MFA_and_OTP_preserves_Google_method()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var start = await Start(client);
        using var first = await Callback(client, root.Google.IssueCode(identity, start.Nonce, start.Pkce), start.State);
        await using (var db = Db()) { var user = await db.Users.SingleAsync(u => u.Email == identity.Email); user.MfaEmailEnabled = true; user.AuthVersion++; await db.SaveChangesAsync(); }
        start = await Start(client); using var callback = await Callback(client, root.Google.IssueCode(identity, start.Nonce, start.Pkce), start.State);
        using var completed = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(Ticket(callback)));
        Assert.Equal(HttpStatusCode.Accepted, completed.StatusCode); Assert.DoesNotContain("accessToken", await completed.Content.ReadAsStringAsync());
        var challenge = (await completed.Content.ReadFromJsonAsync<MfaChallengeResponse>())!;
        using var me = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(me, 401, "AUTH_UNAUTHENTICATED");
        await using var scope = host.Services.CreateAsyncScope(); var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var outbox = await (from o in context.OutboxMessages join a in context.AuditEvents on o.EventId equals a.Id
            join c in context.Challenges on a.SubjectId equals (Guid?)c.UserId where c.Id == challenge.ChallengeId && o.Kind == OutboxKind.MfaEmail select o).SingleAsync();
        var email = scope.ServiceProvider.GetRequiredService<MfaEmailProtector>().Unprotect(outbox.ProtectedPayload);
        using var verified = await client.PostAsJsonAsync("/api/v1/auth/mfa/email/verify", new VerifyOtpRequest(challenge.ChallengeId, email.Code));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode); var result = (await verified.Content.ReadFromJsonAsync<AuthResult>())!;
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken).Claims;
        Assert.Contains(claims, c => c.Type == "amr" && c.Value == "federated"); Assert.Contains(claims, c => c.Type == "amr" && c.Value == "mfa");
    }
    private async Task<AuthResult> LocalLogin(HttpClient client, string email, bool seed = true)
    {
        const string password = "a long secure password 123!";
        if (seed)
        {
            await using var db = Db(); var user = new User { Email = email, DisplayName = "Local user", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, password); db.Users.Add(user); await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = null;
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<AuthResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken); return result;
    }
    private static async Task<(string State, string Nonce, string Pkce, Uri Url)> Protocol(HttpResponseMessage challenge)
    {
        Assert.Equal(HttpStatusCode.OK, challenge.StatusCode);
        var url = new Uri((await challenge.Content.ReadFromJsonAsync<GoogleAuthorizationResponse>())!.AuthorizationUrl);
        var query = QueryHelpers.ParseQuery(url.Query);
        return (query["state"].ToString(), query["nonce"].ToString(), query["code_challenge"].ToString(), url);
    }
    [Fact]
    public async Task Explicit_link_then_unlink_revokes_sessions_and_keeps_password_method()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var local = await LocalLogin(client, identity.Email);
        using var link = await client.PostAsync("/api/v1/auth/google/link", null); var protocol = await Protocol(link);
        using var callback = await Callback(client, root.Google.IssueCode(identity, protocol.Nonce, protocol.Pkce), protocol.State);
        using var complete = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(Ticket(callback))); Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        using var revoked = await client.GetAsync("/api/v1/auth/me"); await ErrorContractTests.AssertProblem(revoked, 401, "AUTH_UNAUTHENTICATED");
        await LocalLogin(client, identity.Email, false); using var unlink = await client.DeleteAsync("/api/v1/auth/google/link"); Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
        await using var db = Db(); Assert.False(await db.ExternalLogins.AnyAsync(x => x.UserId == local.User.Id)); Assert.NotNull((await db.Users.SingleAsync(u => u.Id == local.User.Id)).PasswordHash);
    }
    [Fact]
    public async Task Local_email_collision_requires_explicit_authenticated_link()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var local = await LocalLogin(client, identity.Email);
        client.DefaultRequestHeaders.Authorization = null; var start = await Start(client);
        using var callback = await Callback(client, root.Google.IssueCode(identity, start.Nonce, start.Pkce), start.State);
        Assert.Equal("/google-error", callback.Headers.Location!.AbsolutePath); Assert.Contains("AUTH_GOOGLE_LINK_REQUIRED", callback.Headers.Location.Fragment);
        await using var db = Db(); Assert.False(await db.ExternalLogins.AnyAsync(x => x.UserId == local.User.Id));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Google_reauthentication_requires_fresh_provider_auth_time(bool stale)
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var identity = Identity(); var start = await Start(client);
        using var callback = await Callback(client, root.Google.IssueCode(identity, start.Nonce, start.Pkce), start.State);
        using var complete = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(Ticket(callback)));
        var result = (await complete.Content.ReadFromJsonAsync<AuthResult>())!; client.DefaultRequestHeaders.Authorization = new("Bearer", result.AccessToken);
        using var reauth = await client.PostAsync("/api/v1/auth/google/reauthenticate", null); var protocol = await Protocol(reauth);
        Assert.Equal("0", QueryHelpers.ParseQuery(protocol.Url.Query)["max_age"]);
        using var returned = await Callback(client, root.Google.IssueCode(identity, protocol.Nonce, protocol.Pkce, stale ? GoogleTokenFault.StaleAuthentication : GoogleTokenFault.None), protocol.State);
        if (stale) Assert.Equal("/google-error", returned.Headers.Location!.AbsolutePath);
        else
        {
            using var verified = await client.PostAsJsonAsync("/api/v1/auth/google/complete", new GoogleCompleteRequest(Ticket(returned)));
            Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
        }
    }
    [Fact]
    public async Task Provider_error_description_and_code_are_not_logged_or_reflected()
    {
        await Migrate(); using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture);
        await using var host = Host(root, key); using var client = await Client(host); var start = await Start(client);
        const string secret = "SECRET_PROVIDER_ERROR_DO_NOT_LOG";
        using var response = await client.GetAsync(QueryHelpers.AddQueryString("/signin-google", new Dictionary<string,string?>
        { ["state"] = start.State, ["error"] = "access_denied", ["error_description"] = secret }));
        Assert.DoesNotContain(secret, response.Headers.Location!.ToString());
        Assert.DoesNotContain(secret, string.Join("\n", root.Logs.Entries));
    }
}
