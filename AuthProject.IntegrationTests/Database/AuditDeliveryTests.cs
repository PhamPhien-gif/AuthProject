using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Logging;
using AuthProject.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AuditDeliveryTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
    private readonly AuditPayloadProtector protector = new(new EphemeralDataProtectionProvider());
    private sealed class Factory(string connection, TimeProvider clock) : IDbContextFactory<AuthDbContext>
    { public AuthDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(connection).Options, clock); }
    private Factory DbFactory => new(fixture.ConnectionString, clock);
    private sealed class Sink : IAuditSink
    { public bool Fail { get; set; } public Action? AfterWrite { get; set; } public List<Guid> Events { get; } = []; public void Write(AuditEnvelope envelope) { if (Fail) throw new IOException("DO_NOT_LOG_SECRET"); Events.Add(envelope.EventId); AfterWrite?.Invoke(); } }
    private AuditDispatcher Dispatcher(Sink sink) => new(DbFactory, protector, sink, Options.Create(new AuditDeliveryOptions { MaxAttempts = 2 }), clock, NullLogger<AuditDispatcher>.Instance);
    private async Task<Guid> Stage()
    {
        await using var db = DbFactory.CreateDbContext(); await db.Database.MigrateAsync();
        await db.OutboxMessages.Where(m => m.Kind == OutboxKind.Audit && m.ProcessedAt == null).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeadLetteredAt, clock.GetUtcNow()));
        var id = new AuditWriter(db, protector, clock).Stage(new(AuditEventType.LoginFailed, AuditOutcome.Failure, AuditReason.InvalidCredentials, Guid.NewGuid().ToString("N")));
        await db.SaveChangesAsync(); return id;
    }
    [Fact]
    public async Task Failed_sink_keeps_SQL_event_pending_and_retries_after_backoff()
    {
        var id = await Stage(); var sink = new Sink { Fail = true }; await Dispatcher(sink).DispatchOneAsync();
        await using var db = DbFactory.CreateDbContext(); var row = await db.OutboxMessages.SingleAsync(m => m.EventId == id);
        Assert.Null(row.ProcessedAt); Assert.Equal("SINK_UNAVAILABLE", row.LastErrorCode); Assert.Equal(1, row.Attempts);
        Assert.False(await Dispatcher(sink).DispatchOneAsync()); clock.Advance(TimeSpan.FromSeconds(30)); sink.Fail = false;
        Assert.True(await Dispatcher(sink).DispatchOneAsync()); Assert.Contains(id, sink.Events);
        Assert.NotNull((await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventId == id)).ProcessedAt);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Id == id));
    }
    [Fact]
    public async Task Exhausted_retry_goes_to_dead_letter_without_deleting_durable_audit()
    {
        var id = await Stage(); var sink = new Sink { Fail = true }; await Dispatcher(sink).DispatchOneAsync(); clock.Advance(TimeSpan.FromSeconds(30));
        await Dispatcher(sink).DispatchOneAsync(); await using var db = DbFactory.CreateDbContext();
        Assert.NotNull((await db.OutboxMessages.SingleAsync(m => m.EventId == id)).DeadLetteredAt);
        Assert.Null((await db.OutboxMessages.SingleAsync(m => m.EventId == id)).ProcessedAt);
        Assert.True(await db.AuditEvents.AnyAsync(a => a.Id == id));
    }
    [Fact]
    public async Task Invalid_protected_payload_is_not_sent_and_uses_safe_failure_code()
    {
        var id = await Stage(); await using var db = DbFactory.CreateDbContext();
        await db.OutboxMessages.Where(m => m.EventId == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.ProtectedPayload, "DO_NOT_LOG_SECRET"));
        var sink = new Sink(); await Dispatcher(sink).DispatchOneAsync();
        Assert.DoesNotContain(id, sink.Events); Assert.Equal("PAYLOAD_INVALID", (await db.OutboxMessages.SingleAsync(m => m.EventId == id)).LastErrorCode);
    }
    [Fact]
    public async Task Parallel_dispatchers_only_one_claim_writes_before_lease_expires()
    {
        var id = await Stage(); var sink = new Sink(); await Task.WhenAll(Dispatcher(sink).DispatchOneAsync(), Dispatcher(sink).DispatchOneAsync());
        Assert.Equal(id, Assert.Single(sink.Events));
    }
    [Fact]
    public async Task Lease_expiry_before_ack_retries_with_same_EventId_for_deduplication()
    {
        var id = await Stage(); var sink = new Sink { AfterWrite = () => clock.Advance(TimeSpan.FromSeconds(121)) };
        await Dispatcher(sink).DispatchOneAsync(); await using var db = DbFactory.CreateDbContext();
        Assert.Null((await db.OutboxMessages.SingleAsync(m => m.EventId == id)).ProcessedAt);
        sink.AfterWrite = null; await Dispatcher(sink).DispatchOneAsync(); Assert.Equal(new[] { id, id }, sink.Events);
        Assert.NotNull((await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.EventId == id)).ProcessedAt);
    }
    [Fact]
    public async Task Crashed_last_claim_is_dead_lettered_after_lease_expiry()
    {
        var id = await Stage(); await using var db = DbFactory.CreateDbContext();
        await db.OutboxMessages.Where(m => m.EventId == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, 2).SetProperty(m => m.LeaseUntil, clock.GetUtcNow().AddSeconds(-1)));
        var sink = new Sink(); await Dispatcher(sink).DispatchOneAsync(); Assert.Empty(sink.Events);
        var row = await db.OutboxMessages.SingleAsync(m => m.EventId == id); Assert.NotNull(row.DeadLetteredAt); Assert.Equal("LEASE_EXHAUSTED", row.LastErrorCode);
    }
}
