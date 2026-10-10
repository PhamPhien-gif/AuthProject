using System.Net;
using System.Net.Http.Json;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.IntegrationTests.Foundation;
using AuthProject.IntegrationTests.Hosting;
using AuthProject.Security.Interfaces;
using AuthProject.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AuditHttpTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private WebApplicationFactory<Program> Host(AuthWebApplicationFactory root, TemporaryJwtKey key) => root.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Persistence:Enabled"] = "true", ["Auth:Jwt:Enabled"] = "true", ["Auth:Jwt:Issuer"] = key.Settings.Issuer,
        ["Auth:Jwt:Audience"] = key.Settings.Audience, ["Auth:Jwt:KeyId"] = key.Settings.KeyId, ["Auth:Jwt:SigningKeyPath"] = key.Path,
        ["Auth:Jwt:ClockSkewSeconds"] = "0"
    })));
    private async Task<(User User, string Token, Guid History)> Seed(WebApplicationFactory<Program> host, TimeProvider clock, string state)
    {
        await using var db = Db(); await db.Database.MigrateAsync(); var now = clock.GetUtcNow();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "User", Status = UserStatus.Active, EmailConfirmedAt = now, MfaEmailEnabled = state != "enroll" };
        var session = new AuthSession { UserId = user.Id, AuthVersion = 1, CreatedAt = now, LastSeenAt = now, AuthenticatedAt = now,
            MfaVerifiedAt = state == "enroll" ? null : now, IdleExpiresAt = now.AddDays(1), AbsoluteExpiresAt = now.AddDays(2) };
        var history = new LoginHistory { UserId = user.Id, SessionId = session.Id, OccurredAt = now, Method = AuthenticationMethod.Password, Outcome = AuditOutcome.Success, TraceId = "history", IpAddress = "192.0.2.123", UserAgent = "SECRET_AGENT" };
        db.Users.Add(user); db.Sessions.Add(session); db.UserRoles.Add(new() { UserId = user.Id, RoleId = state == "user" ? SystemRoles.UserId : SystemRoles.AdminId }); db.LoginHistories.Add(history);
        db.AuditEvents.Add(new() { ActorUserId = user.Id, SubjectId = user.Id, OccurredAt = now, EventType = AuditEventType.LoginCompleted, Outcome = AuditOutcome.Success,
            TraceId = "audit", SafeMetadata = "{\"count\":1,\"accessToken\":\"SECRET_METADATA\"}" });
        await db.SaveChangesAsync(); await using var scope = host.Services.CreateAsyncScope();
        return (user, scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>().Issue(session), history.Id);
    }
    [Fact]
    public async Task History_is_owner_scoped_and_masks_legacy_PII_without_returning_raw_agent()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); await using var host = Host(root, key);
        var own = await Seed(host, root.Clock, "user"); var other = await Seed(host, root.Clock, "user");
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") }); client.DefaultRequestHeaders.Authorization = new("Bearer", own.Token);
        using var response = await client.GetAsync("/api/v1/users/me/login-history?userId=" + other.User.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        var page = (await response.Content.ReadFromJsonAsync<PagedResponse<LoginHistoryResponse>>())!; Assert.Equal(own.History, page.Items.Single().Id); Assert.Equal("192.0.2.0/24", page.Items.Single().IpPrefix);
        Assert.DoesNotContain("SECRET_AGENT", await response.Content.ReadAsStringAsync()); Assert.DoesNotContain("192.0.2.123", await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData("user", 403, "AUTH_FORBIDDEN")] [InlineData("enroll", 403, "AUTH_MFA_REQUIRED")]
    [InlineData("anonymous", 401, "AUTH_UNAUTHENTICATED")]
    public async Task Audit_requires_admin_MFA(string state, int status, string code)
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); await using var host = Host(root, key);
        using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        if (state != "anonymous") { var actor = await Seed(host, root.Clock, state); client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token); }
        using var response = await client.GetAsync("/api/v1/admin/audit-logs"); await ErrorContractTests.AssertProblem(response, status, code);
    }
    [Fact]
    public async Task Audit_filters_are_paginated_and_metadata_is_allowlisted()
    {
        using var key = new TemporaryJwtKey(); await using var root = new AuthWebApplicationFactory(database: fixture); await using var host = Host(root, key);
        var actor = await Seed(host, root.Clock, "admin"); using var client = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") }); client.DefaultRequestHeaders.Authorization = new("Bearer", actor.Token);
        using var response = await client.GetAsync($"/api/v1/admin/audit-logs?subjectId={actor.User.Id}&actorUserId={actor.User.Id}&eventType=LoginCompleted&outcome=Success&reason=None&traceId=audit&pageSize=1&sort=occurredAtAsc");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var page = (await response.Content.ReadFromJsonAsync<PagedResponse<AuditResponse>>())!; Assert.Equal(1, page.TotalCount); Assert.Equal(actor.User.Id, page.Items.Single().SubjectId);
        Assert.Equal(1, page.Items.Single().Metadata.Count); Assert.DoesNotContain("SECRET_METADATA", await response.Content.ReadAsStringAsync());
        using var invalid = await client.GetAsync("/api/v1/admin/audit-logs?eventType=1&sort=unsafe&pageSize=101"); await ErrorContractTests.AssertProblem(invalid, 400, "VALIDATION_FAILED");
        using var empty = await client.GetAsync($"/api/v1/admin/audit-logs?subjectId={actor.User.Id}&page=2147483647&pageSize=100"); Assert.Empty((await empty.Content.ReadFromJsonAsync<PagedResponse<AuditResponse>>())!.Items);
    }
}
