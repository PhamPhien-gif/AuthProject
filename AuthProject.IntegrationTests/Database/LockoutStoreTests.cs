using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class LockoutStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock);
    }

    [Fact]
    public async Task ParallelFailures_StopAtThresholdAndAuditCommitsWithCounter_CooldownAllowsSuccess()
    {
        var clock = new FakeTimeProvider();
        var factory = new Factory(fixture.ConnectionString, clock);
        await using var setup = factory.CreateDbContext();
        await setup.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Lockout", Status = UserStatus.Active,
            EmailConfirmedAt = clock.GetUtcNow(), PasswordHash = "test-hash" };
        setup.Users.Add(user);
        await setup.SaveChangesAsync();
        var store = new PasswordAttemptStore(factory, Options.Create(new LockoutOptions()),
            new AuditPayloadProtector(new EphemeralDataProtectionProvider()), clock);
        var snapshot = await store.FindAsync(user.Email);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Fail()
        {
            if (Interlocked.Increment(ref arrivals) == 20) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(await store.CompleteAsync(snapshot, PasswordCheck.Failed, "trace_lockout"));
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Fail()));
        await using var check = factory.CreateDbContext();
        var stored = await check.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(5, stored.FailedLoginCount);
        Assert.Equal(clock.GetUtcNow().AddSeconds(900), stored.LockoutEnd);
        Assert.Equal(20, await check.AuditEvents.CountAsync(x => x.SubjectId == user.Id && x.EventType == AuditEventType.LoginFailed));
        var auditCount = await check.AuditEvents.CountAsync(x => x.SubjectId == user.Id);
        Assert.Equal(21, auditCount);
        Assert.Equal(auditCount, await check.OutboxMessages.CountAsync(x => check.AuditEvents.Any(a => a.Id == x.EventId && a.SubjectId == user.Id)));
        Assert.Null(await store.CompleteAsync(snapshot, PasswordCheck.Success, "trace_locked"));
        clock.Advance(TimeSpan.FromSeconds(900));
        Assert.NotNull(await store.CompleteAsync(snapshot, PasswordCheck.Success, "trace_success"));
        stored = await check.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id);
        Assert.Equal(0, stored.FailedLoginCount);
        Assert.Null(stored.LockoutEnd);
    }

    [Fact]
    public async Task StaleCredentialSnapshot_DoesNotAuthenticateAfterPasswordVersionChanged()
    {
        var clock = new FakeTimeProvider();
        var factory = new Factory(fixture.ConnectionString, clock);
        await using var context = factory.CreateDbContext();
        await context.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Stale", Status = UserStatus.Active,
            EmailConfirmedAt = clock.GetUtcNow(), PasswordHash = "old-hash" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var store = new PasswordAttemptStore(factory, Options.Create(new LockoutOptions()), new AuditPayloadProtector(new EphemeralDataProtectionProvider()), clock);
        var old = await store.FindAsync(user.Email);
        user.PasswordHash = "new-hash";
        user.AuthVersion++;
        await context.SaveChangesAsync();
        Assert.Null(await store.CompleteAsync(old, PasswordCheck.Success, "trace_stale"));
    }

    private sealed class BrokenProtector : IAuditPayloadProtector
    {
        public string Protect(AuditEnvelope envelope) => throw new InvalidOperationException("Simulated protected outbox failure.");
        public AuditEnvelope Unprotect(string value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AuditFailure_DoesNotCommitLockoutCounter()
    {
        var clock = new FakeTimeProvider();
        var factory = new Factory(fixture.ConnectionString, clock);
        await using var context = factory.CreateDbContext();
        await context.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Atomic", Status = UserStatus.Active,
            EmailConfirmedAt = clock.GetUtcNow(), PasswordHash = "test-hash" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var store = new PasswordAttemptStore(factory, Options.Create(new LockoutOptions()), new BrokenProtector(), clock);
        var snapshot = await store.FindAsync(user.Email);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(snapshot, PasswordCheck.Failed, "trace_failure"));
        await using var check = factory.CreateDbContext();
        Assert.Equal(0, (await check.Users.FindAsync(user.Id))!.FailedLoginCount);
        Assert.False(await check.AuditEvents.AnyAsync(x => x.SubjectId == user.Id));
    }
}
