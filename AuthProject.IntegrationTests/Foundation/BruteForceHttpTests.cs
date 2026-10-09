using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AuthProject.Entities;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Foundation;

[Trait("Category", "Integration")]
[Trait("Dependency", "None")]
public sealed class BruteForceHttpTests
{
    private static WebApplicationFactory<Program> Create(Dictionary<string, string?>? settings = null) =>
        FoundationFactory.Create().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestSocketFilter>());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings ?? []));
        });

    [Fact]
    public async Task LoginQuota_WhenIpLimitReached_Returns429AndRetryAfterUntilClockBoundary()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "2" });
        using var client = factory.Client();
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/_tests/p04/login", new { email = "a@example.test", password = "password" })).StatusCode);
        var response = await client.PostAsJsonAsync("/_tests/p04/login", new { email = "b@example.test", password = "password" });
        await ErrorContractTests.AssertProblem(response, 429, "RATE_LIMITED");
        Assert.True(response.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        var clock = Assert.IsType<FakeTimeProvider>(factory.Services.GetRequiredService<TimeProvider>());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/_tests/p04/login", new { email = "b@example.test", password = "password" })).StatusCode);
    }

    [Fact]
    public async Task AccountQuota_AcrossDifferentIps_NormalizesEmailAndRestoresBodyForBinding()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Login:AccountPermitLimit"] = "1" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Test-Socket-Ip", "192.0.2.1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/_tests/p04/login", new { email = " a@example.test ", password = "password" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Socket-Ip");
        client.DefaultRequestHeaders.Add("X-Test-Socket-Ip", "192.0.2.2");
        await ErrorContractTests.AssertProblem(await client.PostAsJsonAsync("/_tests/p04/login", new { email = "A@EXAMPLE.TEST", password = "password" }), 429, "RATE_LIMITED");
    }

    [Theory]
    [InlineData(false, "192.0.2.1", "192.0.2.1")]
    [InlineData(true, "192.0.2.1", "192.0.2.1")]
    [InlineData(true, "10.0.0.1", "198.51.100.3")]
    public async Task ForwardedIp_OnlyChangesForExplicitlyTrustedPeer(bool enabled, string socket, string expected)
    {
        await using var factory = Create(new() { ["TrustedProxy:Enabled"] = enabled.ToString(), ["TrustedProxy:KnownProxies:0"] = "10.0.0.1" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Test-Socket-Ip", socket);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.3");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/_tests/p04/ip");
        Assert.Equal(expected, json.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task ForgedForwardedHeader_DoesNotChangeIpQuota()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "1" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.3");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/_tests/p04/login", new { email = "a@example.test", password = "password" })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.4");
        await ErrorContractTests.AssertProblem(await client.PostAsJsonAsync("/_tests/p04/login", new { email = "b@example.test", password = "password" }), 429, "RATE_LIMITED");
    }

    [Fact]
    public async Task CredentialErrors_UnknownWrongAndLocked_HaveSameStatusCodeAndPublicBody()
    {
        await using var factory = Create().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped<IPasswordAttemptStore, FakeAttemptStore>()));
        using var client = factory.Client();
        string? title = null;
        foreach (var email in new[] { "unknown@example.test", "wrong@example.test", "locked@example.test" })
        {
            var response = await client.PostAsJsonAsync("/_tests/p04/credential", new { email, password = "wrong password" });
            var json = await ErrorContractTests.AssertProblem(response, 401, "AUTH_INVALID_CREDENTIALS");
            title ??= json.GetProperty("title").GetString();
            Assert.Equal(title, json.GetProperty("title").GetString());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(email, body);
            Assert.DoesNotContain("lockout", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeAttemptStore(IPasswordHashService hasher, TimeProvider clock) : IPasswordAttemptStore
    {
        public Task<User?> FindAsync(string email, CancellationToken ct = default)
        {
            if (email.StartsWith("unknown", StringComparison.Ordinal)) return Task.FromResult<User?>(null);
            var user = new User { Email = email, Status = UserStatus.Active, EmailConfirmedAt = clock.GetUtcNow(),
                LockoutEnd = email.StartsWith("locked", StringComparison.Ordinal) ? clock.GetUtcNow().AddMinutes(15) : null };
            user.PasswordHash = hasher.Hash(user, "correct password");
            return Task.FromResult<User?>(user);
        }
        public Task<PasswordAcceptance?> CompleteAsync(User? snapshot, PasswordCheck check, string trace, CancellationToken ct = default)
        {
            if (snapshot is not null) LockoutRules.Apply(snapshot, check != PasswordCheck.Failed, clock.GetUtcNow(), new LockoutOptions());
            return Task.FromResult<PasswordAcceptance?>(null);
        }
    }

    [Theory]
    [InlineData("register", "Register")]
    [InlineData("otp", "Otp")]
    [InlineData("refresh", "Refresh")]
    [InlineData("recovery", "Recovery")]
    [InlineData("google", "Google")]
    public async Task NamedPolicy_IndependentlyEnforcesItsQuota(string route, string policy)
    {
        await using var factory = Create(new() { [$"BruteForce:RateLimit:Policies:{policy}:IpPermitLimit"] = "1" });
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/_tests/p04/" + route, null)).StatusCode);
        await ErrorContractTests.AssertProblem(await client.PostAsync("/_tests/p04/" + route, null), 429, "RATE_LIMITED");
    }

    [Fact]
    public async Task ApiQuota_AppliesToUnknownRoutesBeforeBodyProcessing()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Api:IpPermitLimit"] = "1" });
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/unknown-p04")).StatusCode);
        await ErrorContractTests.AssertProblem(await client.GetAsync("/another-unknown-p04"), 429, "RATE_LIMITED");
    }

    [Fact]
    public async Task HttpBurst_GrantsOnlyConfiguredNumberOfRequests()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Login:IpPermitLimit"] = "2" });
        using var client = factory.Client();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            client.PostAsJsonAsync("/_tests/p04/login", new { email = "burst@example.test", password = "password" })));
        Assert.Equal(2, responses.Count(x => x.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(10, responses.Count(x => x.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task StructuredJsonAndAlternateFieldCase_DoNotBypassAccountQuota()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Login:AccountPermitLimit"] = "1" });
        using var client = factory.Client();
        using var body = new StringContent("{\"Email\":\"a@example.test\",\"Password\":\"password\"}", System.Text.Encoding.UTF8, "application/vnd.auth+json");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/_tests/p04/login", body)).StatusCode);
        await ErrorContractTests.AssertProblem(await client.PostAsJsonAsync("/_tests/p04/login", new { email = "A@EXAMPLE.TEST", password = "password" }), 429, "RATE_LIMITED");
    }

    [Fact]
    public async Task RefreshSessionQuota_UsesValidatedPrincipalAcrossIps()
    {
        await using var factory = Create(new() { ["BruteForce:RateLimit:Policies:Refresh:SessionPermitLimit"] = "1" });
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("X-Test-Validated-Session", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/_tests/p04/refresh", null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Socket-Ip", "192.0.2.2");
        await ErrorContractTests.AssertProblem(await client.PostAsync("/_tests/p04/refresh", null), 429, "RATE_LIMITED");
    }
}
