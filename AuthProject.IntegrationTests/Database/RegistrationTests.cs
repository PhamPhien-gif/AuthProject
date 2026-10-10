using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Services;
using Microsoft.EntityFrameworkCore;
using AuthProject.Configuration;
using AuthProject.DTOs.Validators;
using AuthProject.Logging;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using AuthProject.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class RegistrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider clock = new();
    private readonly EmailPayloadProtector emailProtector = new(new EphemeralDataProtectionProvider());
    private readonly AuditPayloadProtector auditProtector = new(new EphemeralDataProtectionProvider());
    private readonly RegistrationOptions settings = new() { Enabled = true, VerificationUrl = "https://client.example.test/verify-email" };
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock);
    }
    private Factory CreateFactory() => new(fixture.ConnectionString, clock);
    private RegistrationStore CreateStore() => new(CreateFactory(), Options.Create(settings), emailProtector, auditProtector, clock);
    private RegistrationService CreateService() => new(CreateStore(), new PasswordHashService(Options.Create(new PasswordHasherOptions { IterationCount = 210000 })),
        Options.Create(settings), new RegisterRequestValidator(), new EmailRequestValidator(), new VerifyEmailRequestValidator());
    private async Task<AuthDbContext> SetupAsync()
    {
        var db = CreateFactory().CreateDbContext();
        await db.Database.MigrateAsync();
        // Tests in this class share a fixture DB, but never share pending deliveries.
        await db.OutboxMessages.Where(x => x.Kind == OutboxKind.Email && x.ProcessedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ProcessedAt, clock.GetUtcNow()));
        return db;
    }
    private async Task<VerificationEmail> LatestEmailAsync(AuthDbContext db, string email)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.NormalizedEmail == EmailNormalizer.Normalize(email));
        var challenge = await db.Challenges.AsNoTracking().Where(x => x.UserId == user.Id && x.Purpose == ChallengePurpose.VerifyEmail)
            .OrderByDescending(x => x.CreatedAt).FirstAsync();
        return emailProtector.Unprotect((await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Kind == OutboxKind.Email && x.EventId == challenge.Id)).ProtectedPayload);
    }
    [Fact]
    public async Task Register_CommitsPendingUserDefaultRoleAndEmailOutbox()
    {
        await using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        await context.Database.MigrateAsync();
        var email = Guid.NewGuid() + "@example.test";
        var service = CreateService();
        await service.RegisterAsync(new RegisterRequest(email, "a long secure password 123!", " Test "), "trace_register");
        var user = await context.Users.SingleAsync(x => x.Email == email);
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.Null(user.EmailConfirmedAt);
        Assert.Equal("Test", user.DisplayName);
        Assert.NotEqual("a long secure password 123!", user.PasswordHash);
        Assert.Single(await context.UserRoles.Where(x => x.UserId == user.Id).ToArrayAsync());
        Assert.Single(await context.Challenges.Where(x => x.UserId == user.Id).ToArrayAsync());
        Assert.True(await context.OutboxMessages.AnyAsync(x => x.Kind == OutboxKind.Email));
    }

    [Fact]
    public async Task ConcurrentDuplicateRegistration_CreatesOneAccountAndNeverChangesCredentials()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Register(int i)
        {
            if (Interlocked.Increment(ref arrivals) == 6) start.SetResult();
            await start.Task;
            await CreateService().RegisterAsync(new(i % 2 == 0 ? email.ToUpperInvariant() : email, "a long secure password 123!", "User"), "trace_race");
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(Register));
        var user = await db.Users.SingleAsync(x => x.NormalizedEmail == EmailNormalizer.Normalize(email));
        var hash = user.PasswordHash;
        await CreateService().RegisterAsync(new(email, "another very secure password 456!", "Changed"), "trace_duplicate");
        Assert.Equal(hash, (await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id)).PasswordHash);
        Assert.Single(await db.Challenges.Where(x => x.UserId == user.Id).ToArrayAsync());
        Assert.Equal(SystemRoles.UserId, (await db.UserRoles.SingleAsync(x => x.UserId == user.Id)).RoleId);
        Assert.Single(await db.AuditEvents.Where(x => x.SubjectId == user.Id && x.EventType == AuditEventType.Registered).ToArrayAsync());
    }

    [Fact]
    public async Task Verify_ConcurrentConsumeHasOneWinnerAndCreatesNoSession()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        await CreateService().RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_verify");
        var message = await LatestEmailAsync(db, email);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<bool> Verify()
        {
            if (Interlocked.Increment(ref arrivals) == 2) start.SetResult();
            await start.Task;
            return await CreateStore().VerifyAsync(EmailVerificationToken.Digest(message.Token)!, "trace_verify", default);
        }
        var results = await Task.WhenAll(Verify(), Verify());
        Assert.Single(results, x => x);
        var user = await db.Users.SingleAsync(x => x.Id == message.UserId);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(clock.GetUtcNow(), user.EmailConfirmedAt);
        Assert.False(await db.Sessions.AnyAsync(x => x.UserId == user.Id));
        Assert.Single(await db.AuditEvents.Where(x => x.SubjectId == user.Id && x.EventType == AuditEventType.EmailVerified).ToArrayAsync());
        var error = await Assert.ThrowsAsync<AppException>(() => CreateService().VerifyAsync(new(message.Token), "trace_replay"));
        Assert.Equal("AUTH_CHALLENGE_INVALID", error.Error.Code);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("purpose")]
    [InlineData("version")]
    [InlineData("disabled")]
    public async Task Verify_InvalidStateIsGenericAndCannotActivate(string state)
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        await CreateService().RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_invalid");
        var message = await LatestEmailAsync(db, email);
        var challenge = await db.Challenges.SingleAsync(x => x.Id == message.ChallengeId);
        var user = await db.Users.SingleAsync(x => x.Id == message.UserId);
        if (state == "expired") clock.Advance(TimeSpan.FromSeconds(settings.TokenLifetimeSeconds));
        if (state == "purpose") challenge.Purpose = ChallengePurpose.ResetPassword;
        if (state == "version") user.AuthVersion++;
        if (state == "disabled") user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<AppException>(() => CreateService().VerifyAsync(new(message.Token), "trace_invalid"));
        Assert.Equal("AUTH_CHALLENGE_INVALID", error.Error.Code);
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id)).EmailConfirmedAt);
    }

    [Fact]
    public async Task Resend_CooldownAndSendBudgetApplyOldTokenIsInvalidatedAndBudgetRecovers()
    {
        await using var db = await SetupAsync();
        var service = CreateService();
        var email = Guid.NewGuid() + "@example.test";
        await service.RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_resend");
        var first = await LatestEmailAsync(db, email);
        await service.ResendAsync(new(email), "trace_cooldown");
        Assert.Equal(first.ChallengeId, (await LatestEmailAsync(db, email)).ChallengeId);
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.ResendAsync(new(email), "trace_resend");
        var second = await LatestEmailAsync(db, email);
        Assert.NotEqual(first.Token, second.Token);
        await Assert.ThrowsAsync<AppException>(() => service.VerifyAsync(new(first.Token), "trace_old"));
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.ResendAsync(new(email), "trace_resend");
        var third = await LatestEmailAsync(db, email);
        clock.Advance(TimeSpan.FromSeconds(60));
        await service.ResendAsync(new(email), "trace_budget");
        Assert.Equal(third.ChallengeId, (await LatestEmailAsync(db, email)).ChallengeId);
        clock.Advance(TimeSpan.FromSeconds(900));
        await service.ResendAsync(new(email), "trace_new_window");
        Assert.NotEqual(third.ChallengeId, (await LatestEmailAsync(db, email)).ChallengeId);
        await service.ResendAsync(new(Guid.NewGuid() + "@example.test"), "trace_unknown");
    }

    private sealed class RecordingTransport : IEmailTransport
    {
        public int Failures { get; set; }
        public List<Guid> Sent { get; } = [];
        public Task SendAsync(Guid id, string recipient, EmailContent content, CancellationToken token)
        {
            if (Failures-- > 0) throw new InvalidOperationException("Simulated SMTP failure with secret text");
            Sent.Add(id);
            return Task.CompletedTask;
        }
    }
    private EmailOutboxDispatcher Dispatcher(RecordingTransport transport) => new(CreateFactory(), emailProtector, transport, Options.Create(settings), clock,
        NullLogger<EmailOutboxDispatcher>.Instance);

    [Fact]
    public async Task SmtpFailure_PreservesUserAndRetriesSameMessageWithoutDuplicateAccount()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        await CreateService().RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_smtp");
        var message = await LatestEmailAsync(db, email);
        var transport = new RecordingTransport { Failures = 1 };
        Assert.True(await Dispatcher(transport).DispatchOneAsync());
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Kind == OutboxKind.Email && x.EventId == message.ChallengeId);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.ProcessedAt);
        Assert.Equal(clock.GetUtcNow().AddSeconds(30), row.NextAttemptAt);
        Assert.False(await Dispatcher(transport).DispatchOneAsync());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await Dispatcher(transport).DispatchOneAsync());
        Assert.Single(transport.Sent);
        Assert.Equal(row.Id, transport.Sent[0]);
        Assert.NotNull((await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == row.Id)).ProcessedAt);
        Assert.Single(await db.Users.Where(x => x.Id == message.UserId).ToArrayAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("replaced")]
    [InlineData("verified")]
    public async Task Dispatcher_NeverSendsExpiredReplacedOrConsumedToken(string state)
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        var service = CreateService();
        await service.RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_stale_mail");
        var message = await LatestEmailAsync(db, email);
        if (state == "expired") clock.Advance(TimeSpan.FromSeconds(settings.TokenLifetimeSeconds));
        if (state == "replaced") { clock.Advance(TimeSpan.FromSeconds(60)); await service.ResendAsync(new(email), "trace_replace"); }
        if (state == "verified") await service.VerifyAsync(new(message.Token), "trace_verify_mail");
        var transport = new RecordingTransport();
        Assert.True(await Dispatcher(transport).DispatchOneAsync());
        Assert.Empty(transport.Sent);
        Assert.NotNull((await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EventId == message.ChallengeId && x.Kind == OutboxKind.Email)).ProcessedAt);
    }

    [Fact]
    public async Task ParallelDispatch_ClaimsOneDeliveryAndIgnoresAuditOutbox()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        await CreateService().RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_dispatch");
        var transport = new RecordingTransport();
        await Task.WhenAll(Dispatcher(transport).DispatchOneAsync(), Dispatcher(transport).DispatchOneAsync());
        Assert.Single(transport.Sent);
        Assert.True(await db.OutboxMessages.AnyAsync(x => x.Kind == OutboxKind.Audit && x.ProcessedAt == null));
    }

    private sealed class BrokenEmailProtector : IEmailPayloadProtector
    {
        public string Protect(VerificationEmail email) => throw new InvalidOperationException("Simulated key failure.");
        public VerificationEmail Unprotect(string payload) => throw new NotSupportedException();
    }
    [Fact]
    public async Task EmailProtectionFailure_RollsBackRegistrationAndAudit()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        var store = new RegistrationStore(CreateFactory(), Options.Create(settings), new BrokenEmailProtector(), auditProtector, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RegisterAsync(email, "User", "test_hash", "trace_rollback", default));
        Assert.False(await db.Users.AnyAsync(x => x.Email == email));
        Assert.False(await db.AuditEvents.AnyAsync(x => x.TraceId == "trace_rollback"));
    }

    [Fact]
    public async Task AbandonedLease_IsRecoveredAtExpiryAndStopsAfterTenFailures()
    {
        await using var db = await SetupAsync();
        var email = Guid.NewGuid() + "@example.test";
        await CreateService().RegisterAsync(new(email, "a long secure password 123!", "User"), "trace_lease");
        var message = await LatestEmailAsync(db, email);
        var row = await db.OutboxMessages.SingleAsync(x => x.EventId == message.ChallengeId && x.Kind == OutboxKind.Email);
        row.LeaseUntil = clock.GetUtcNow().AddSeconds(120);
        row.Attempts = 9;
        await db.SaveChangesAsync();
        var transport = new RecordingTransport { Failures = 1 };
        Assert.False(await Dispatcher(transport).DispatchOneAsync());
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.True(await Dispatcher(transport).DispatchOneAsync());
        var stored = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(10, stored.Attempts);
        Assert.Null(stored.ProcessedAt);
        clock.Advance(TimeSpan.FromSeconds(900));
        Assert.False(await Dispatcher(transport).DispatchOneAsync());
        Assert.Empty(transport.Sent);
    }
}
