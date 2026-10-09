using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Data.Configurations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class PersistenceTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private AuthDbContext CreateContext() => new(new DbContextOptionsBuilder<AuthDbContext>()
        .UseSqlServer(fixture.ConnectionString).Options);

    [Fact]
    public async Task Users_WhenEquivalentEmailsInsertedConcurrently_OnlyOneCommits()
    {
        await using var first = CreateContext();
        await first.Database.MigrateAsync();
        await using var second = CreateContext();
        var email = Guid.NewGuid() + "@example.test";
        first.Users.Add(new User { Email = "  " + email.ToLowerInvariant() + "  ", DisplayName = "First" });
        second.Users.Add(new User { Email = email.ToUpperInvariant(), DisplayName = "Second" });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        async Task<bool> Commit(AuthDbContext context)
        {
            if (Interlocked.Increment(ref readyCount) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try { await context.SaveChangesAsync(); return true; }
            catch (DbUpdateException error) when (error.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            { return false; }
        }
        var results = await Task.WhenAll(Commit(first), Commit(second));
        Assert.Single(results, x => x);
        await using var check = CreateContext();
        var stored = Assert.Single(await check.Users.Where(x => x.NormalizedEmail == email.ToUpperInvariant()).ToArrayAsync());
        Assert.Equal(stored.Email.Trim(), stored.Email);
    }

    [Fact]
    public async Task User_WhenUpdatedFromStaleContext_RejectsLostUpdate()
    {
        await using var first = CreateContext();
        await first.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Original" };
        first.Users.Add(user);
        await first.SaveChangesAsync();
        Assert.Equal(8, user.RowVersion.Length);
        await using var second = CreateContext();
        var stale = await second.Users.SingleAsync(x => x.Id == user.Id);
        user.DisplayName = "Committed";
        await first.SaveChangesAsync();
        stale.DisplayName = "Lost";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var check = CreateContext();
        Assert.Equal("Committed", (await check.Users.FindAsync(user.Id))!.DisplayName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transaction_CommitsOrRollsBackStateAuditAndProtectedOutboxTogether(bool commit)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var protector = new AuditPayloadProtector(new EphemeralDataProtectionProvider());
        var writer = new AuditWriter(context, protector, TimeProvider.System);
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Atomic" };
        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Users.Add(user);
        var eventId = writer.Stage(new(AuditEventType.Registered, AuditOutcome.Success, AuditReason.None,
            "trace_123", user.Id, user.Id));
        await context.SaveChangesAsync();
        if (commit) await transaction.CommitAsync();
        else await transaction.RollbackAsync();
        await using var check = CreateContext();
        Assert.Equal(commit, await check.Users.AnyAsync(x => x.Id == user.Id));
        Assert.Equal(commit, await check.AuditEvents.AnyAsync(x => x.Id == eventId));
        Assert.Equal(commit, await check.OutboxMessages.AnyAsync(x => x.EventId == eventId));
        if (commit)
        {
            var payload = (await check.OutboxMessages.SingleAsync(x => x.EventId == eventId)).ProtectedPayload;
            Assert.DoesNotContain("trace_123", payload);
            Assert.Equal(eventId, protector.Unprotect(payload).EventId);
        }
    }

    [Fact]
    public async Task Audit_WhenChangedOrDeleted_RejectsMutation()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var writer = new AuditWriter(context, new AuditPayloadProtector(new EphemeralDataProtectionProvider()), TimeProvider.System);
        var id = writer.Stage(new(AuditEventType.LoginFailed, AuditOutcome.Failure, AuditReason.InvalidCredentials, "trace_123"));
        await context.SaveChangesAsync();
        var audit = await context.AuditEvents.SingleAsync(x => x.Id == id);
        audit.Outcome = AuditOutcome.Success;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        context.AuditEvents.Remove(await context.AuditEvents.SingleAsync(x => x.Id == id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task UnitOfWork_WhenOperationThrowsAfterSaving_RollsBackAllRowsAndClearsTracking()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Rollback" };
        var writer = new AuditWriter(context, new AuditPayloadProtector(new EphemeralDataProtectionProvider()), TimeProvider.System);
        Guid eventId = default;
        var work = new AuthUnitOfWork(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ExecuteAsync<int>(async ct =>
        {
            context.Users.Add(user);
            eventId = writer.Stage(new(AuditEventType.Registered, AuditOutcome.Success, AuditReason.None, "trace_123", user.Id));
            await work.SaveChangesAsync(ct);
            throw new InvalidOperationException("Simulated operation failure after SQL write.");
        }));
        Assert.Empty(context.ChangeTracker.Entries());
        await using var check = CreateContext();
        Assert.False(await check.Users.AnyAsync(x => x.Id == user.Id));
        Assert.False(await check.AuditEvents.AnyAsync(x => x.Id == eventId));
        Assert.False(await check.OutboxMessages.AnyAsync(x => x.EventId == eventId));
    }

    [Fact]
    public async Task Repositories_WhenProfileQueried_ProjectsRolesWithoutTrackingAndAuthLookupNormalizesEmail()
    {
        await using var setup = CreateContext();
        await setup.Database.MigrateAsync();
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Profile", PasswordHash = "internal-hash" };
        var roles = new RoleRepository(setup);
        var role = await roles.FindByNameAsync(" user ");
        Assert.NotNull(role);
        Assert.Empty(setup.ChangeTracker.Entries());
        await new AuthUnitOfWork(setup).ExecuteAsync(ct =>
        {
            new UserRepository(setup).Add(user);
            roles.Assign(user.Id, role.Id);
            return Task.FromResult(user.Id);
        });
        await using var read = CreateContext();
        var repository = new UserRepository(read);
        var profile = await repository.GetProfileAsync(user.Id);
        Assert.NotNull(profile);
        Assert.Equal(user.Id, profile.Id);
        Assert.Equal(new[] { SystemRoles.User }, profile.Roles);
        Assert.Empty(read.ChangeTracker.Entries());
        Assert.DoesNotContain("internal-hash", System.Text.Json.JsonSerializer.Serialize(profile));
        var tracked = await repository.FindForAuthenticationAsync(" " + user.Email.ToUpperInvariant() + " ");
        Assert.Equal(user.Id, tracked!.Id);
        Assert.Single(read.ChangeTracker.Entries<User>());
        Assert.Null(await repository.GetProfileAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RefreshTokens_WhenActiveTokenExists_RejectsSecondUntilFirstConsumedAndRestrictsUserDeletion()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Session" };
        var session = new AuthSession { UserId = user.Id, AuthVersion = 1, CreatedAt = now,
            AuthenticatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30) };
        RefreshToken NewToken() => new() { SessionId = session.Id,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()))),
            CreatedAt = now, ExpiresAt = now.AddDays(7) };
        context.Users.Add(user);
        new SessionRepository(context).Add(session);
        var first = NewToken();
        new RefreshTokenRepository(context).Add(first);
        await context.SaveChangesAsync();
        var second = NewToken();
        context.RefreshTokens.Add(second);
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var sqlError = Assert.IsType<Microsoft.Data.SqlClient.SqlException>(duplicate.InnerException);
        Assert.Contains(sqlError.Number, new[] { 2601, 2627 });
        context.Entry(second).State = EntityState.Detached;
        first.ConsumedAt = now;
        await context.SaveChangesAsync();
        context.RefreshTokens.Add(second);
        await context.SaveChangesAsync();
        first.ReplacedById = second.Id;
        await context.SaveChangesAsync();
        await using var other = CreateContext();
        Assert.Equal(session.Id, (await new SessionRepository(other).FindForUpdateAsync(session.Id))!.Id);
        Assert.Equal(second.Id, (await new RefreshTokenRepository(other).FindForUpdateAsync(second.TokenHash))!.Id);
        other.ChangeTracker.Clear(); // Check the database FK, independently of EF tracked dependents.
        other.Users.Remove((await other.Users.FindAsync(user.Id))!);
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.True(await context.Sessions.AnyAsync(x => x.Id == session.Id));
    }

    [Fact]
    public async Task ExternalLogins_WhenProviderSubjectAlreadyLinked_RejectsDifferentUser()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var first = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "First" };
        var second = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Second" };
        var subject = Guid.NewGuid().ToString();
        context.Users.AddRange(first, second);
        context.ExternalLogins.Add(new ExternalLogin { UserId = first.Id, Provider = "Google", ProviderSubject = subject, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        context.ExternalLogins.Add(new ExternalLogin { UserId = second.Id, Provider = "Google", ProviderSubject = subject, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task RefreshTokens_WhenSameDigestHasDifferentCase_RejectsDuplicateAcrossSessions()
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Digest" };
        AuthSession NewSession() => new() { UserId = user.Id, AuthVersion = 1, CreatedAt = now,
            AuthenticatedAt = now, LastSeenAt = now, IdleExpiresAt = now.AddDays(7), AbsoluteExpiresAt = now.AddDays(30) };
        var first = NewSession();
        var second = NewSession();
        var digest = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        context.Users.Add(user);
        context.Sessions.AddRange(first, second);
        context.RefreshTokens.Add(new RefreshToken { SessionId = first.Id, TokenHash = digest.ToLowerInvariant(), CreatedAt = now, ExpiresAt = now.AddDays(7) });
        await context.SaveChangesAsync();
        context.RefreshTokens.Add(new RefreshToken { SessionId = second.Id, TokenHash = digest, CreatedAt = now, ExpiresAt = now.AddDays(7) });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData(0, 0, UserStatus.PendingVerification)]
    [InlineData(1, -1, UserStatus.PendingVerification)]
    [InlineData(1, 0, (UserStatus)99)]
    public async Task Users_WhenSecurityCountersOrStatusInvalid_RejectsAtSqlConstraint(long version, int failures, UserStatus status)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.Users.Add(new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Invalid",
            AuthVersion = version, FailedLoginCount = failures, Status = status });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(547, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(error.InnerException).Number);
    }
}
