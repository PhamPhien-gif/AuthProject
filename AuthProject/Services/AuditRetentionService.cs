using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace AuthProject.Services;

public sealed class AuditRetentionService(IDbContextFactory<AuthDbContext> factory, IOptions<AuditRetentionOptions> options,
    IOptions<AuditLogOptions> files, IHostEnvironment environment, TimeProvider clock)
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var now = clock.GetUtcNow(); var o = options.Value; var count = 0;
        // Only the maintenance path may bulk-delete append-only rows under the retention policy.
        var history = await db.LoginHistories.Where(h => h.OccurredAt < now.AddDays(-o.LoginHistoryDays)).OrderBy(h => h.OccurredAt).Take(o.BatchSize).Select(h => h.Id).ToArrayAsync(ct);
        count += await db.LoginHistories.Where(h => history.Contains(h.Id)).ExecuteDeleteAsync(ct);
        var processed = await db.OutboxMessages.Where(m => m.ProcessedAt < now.AddDays(-o.ProcessedOutboxDays) && (m.LeaseUntil == null || m.LeaseUntil <= now))
            .OrderBy(m => m.ProcessedAt).Take(o.BatchSize).Select(m => m.Id).ToArrayAsync(ct);
        count += await db.OutboxMessages.Where(m => processed.Contains(m.Id) && m.ProcessedAt != null).ExecuteDeleteAsync(ct);
        var audit = await db.AuditEvents.Where(a => a.OccurredAt < now.AddDays(-o.AuditDays)
            && !db.OutboxMessages.Any(m => m.EventId == a.Id && m.ProcessedAt == null)).OrderBy(a => a.OccurredAt).Take(o.BatchSize).Select(a => a.Id).ToArrayAsync(ct);
        count += await db.AuditEvents.Where(a => audit.Contains(a.Id) && !db.OutboxMessages.Any(m => m.EventId == a.Id && m.ProcessedAt == null)).ExecuteDeleteAsync(ct);
        var legacy = await db.LoginHistories.AsNoTracking().Where(h => h.UserAgent != null || h.IpAddress != null && !h.IpAddress.Contains("/"))
            .OrderBy(h => h.Id).Take(o.BatchSize).Select(h => new { h.Id, h.IpAddress }).ToArrayAsync(ct);
        foreach (var row in legacy) await db.LoginHistories.Where(h => h.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.IpAddress, HistoryPrivacy.MaskIp(row.IpAddress)).SetProperty(h => h.UserAgent, (string?)null), ct);
        var cutoff = now.AddDays(-o.TokenGraceDays);
        var owners = await db.Sessions.Where(s => (s.RevokedAt < cutoff || s.AbsoluteExpiresAt < cutoff || s.IdleExpiresAt < cutoff)
            && (db.RefreshTokens.Any(t => t.SessionId == s.Id) || !db.LoginHistories.Any(h => h.SessionId == s.Id))).Select(s => s.UserId)
            .Union(db.Challenges.Where(c => c.ExpiresAt < cutoff).Select(c => c.UserId)).Distinct().OrderBy(id => id).Take(o.BatchSize).ToArrayAsync(ct);
        foreach (var owner in owners)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {owner}").SingleOrDefaultAsync(ct);
            var sessions = await db.Sessions.Where(s => s.UserId == owner && (s.RevokedAt < cutoff || s.AbsoluteExpiresAt < cutoff || s.IdleExpiresAt < cutoff)
                && (db.RefreshTokens.Any(t => t.SessionId == s.Id) || !db.LoginHistories.Any(h => h.SessionId == s.Id))).OrderBy(s => s.Id).Take(o.BatchSize).Select(s => s.Id).ToArrayAsync(ct);
            // Delete token self references together only for a terminal family; live consumed tokens remain.
            count += await db.RefreshTokens.Where(t => sessions.Contains(t.SessionId)).ExecuteDeleteAsync(ct);
            var challenges = await db.Challenges.Where(c => c.UserId == owner && c.ExpiresAt < cutoff).OrderBy(c => c.ExpiresAt).Take(o.BatchSize).Select(c => c.Id).ToArrayAsync(ct);
            count += await db.Challenges.Where(c => challenges.Contains(c.Id)).ExecuteDeleteAsync(ct);
            count += await db.Sessions.Where(s => sessions.Contains(s.Id) && !db.LoginHistories.Any(h => h.SessionId == s.Id) && !db.RefreshTokens.Any(t => t.SessionId == s.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        CleanupFiles(); return count;
    }
    private void CleanupFiles()
    {
        if (!files.Value.Enabled) return;
        var directory = Path.GetFullPath(files.Value.Directory, environment.ContentRootPath);
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "audit-*.jsonl*", SearchOption.TopDirectoryOnly))
        {
            if (!Regex.IsMatch(Path.GetFileName(path), @"\Aaudit-[A-Za-z0-9_-]+\.jsonl(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            if (File.GetLastWriteTimeUtc(path) < clock.GetUtcNow().AddDays(-files.Value.FileRetentionDays).UtcDateTime) File.Delete(path);
        }
    }
}
