using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AuthProject.Repositories;

public sealed class AuditQueryStore(IDbContextFactory<AuthDbContext> factory, TimeProvider clock)
{
    public async Task<PagedResponse<LoginHistoryResponse>> HistoryAsync(PasswordActor actor, HistoryQuery paging, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor.UserId, ct);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == actor.SessionId, ct);
        if (AdminAccessRules.SessionError(user, session, actor, clock.GetUtcNow()) is { } error) throw new AppException(error);
        var query = db.LoginHistories.AsNoTracking().Where(h => h.UserId == actor.UserId);
        if (paging.From is { } from) query = query.Where(h => h.OccurredAt >= from);
        if (paging.To is { } to) query = query.Where(h => h.OccurredAt <= to);
        var total = await query.CountAsync(ct); var offset = (long)(paging.Page - 1) * paging.PageSize;
        var ordered = paging.Sort == "occurredAtAsc" ? query.OrderBy(h => h.OccurredAt).ThenBy(h => h.Id) : query.OrderByDescending(h => h.OccurredAt).ThenByDescending(h => h.Id);
        var rows = offset > int.MaxValue ? [] : await ordered.Skip((int)offset).Take(paging.PageSize)
            .Select(h => new { h.Id, h.OccurredAt, h.Method, h.Outcome, h.ReasonCode, h.SessionId, h.IpAddress }).ToArrayAsync(ct);
        return new(rows.Select(h => new LoginHistoryResponse(h.Id, h.OccurredAt, h.Method.ToString(), h.Outcome.ToString(), h.ReasonCode.ToString(), h.SessionId, HistoryPrivacy.MaskIp(h.IpAddress))).ToArray(), paging.Page, paging.PageSize, total);
    }
    public async Task<PagedResponse<AuditResponse>> AuditAsync(AuditQuery paging, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var query = db.AuditEvents.AsNoTracking();
        if (paging.From is { } from) query = query.Where(a => a.OccurredAt >= from);
        if (paging.To is { } to) query = query.Where(a => a.OccurredAt <= to);
        if (paging.ActorUserId is { } actor) query = query.Where(a => a.ActorUserId == actor);
        if (paging.SubjectId is { } subject) query = query.Where(a => a.SubjectId == subject);
        if (paging.EventType is { } e) { var v = Enum.Parse<AuditEventType>(e); query = query.Where(a => a.EventType == v); }
        if (paging.Outcome is { } o) { var v = Enum.Parse<AuditOutcome>(o); query = query.Where(a => a.Outcome == v); }
        if (paging.Reason is { } r) { var v = Enum.Parse<AuditReason>(r); query = query.Where(a => a.ReasonCode == v); }
        if (paging.TraceId is { } trace) query = query.Where(a => a.TraceId == trace);
        var total = await query.CountAsync(ct); var offset = (long)(paging.Page - 1) * paging.PageSize;
        var ordered = paging.Sort == "occurredAtAsc" ? query.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id) : query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id);
        var rows = offset > int.MaxValue ? [] : await ordered.Skip((int)offset).Take(paging.PageSize)
            .Select(a => new { a.Id, a.OccurredAt, a.ActorUserId, a.SubjectId, a.EventType, a.Outcome, a.ReasonCode, a.TraceId, a.SafeMetadata }).ToArrayAsync(ct);
        return new(rows.Select(a => new AuditResponse(a.Id, a.OccurredAt, a.ActorUserId, a.SubjectId, a.EventType.ToString(), a.Outcome.ToString(), a.ReasonCode.ToString(), a.TraceId, Metadata(a.SafeMetadata))).ToArray(), paging.Page, paging.PageSize, total);
    }
    private static AuditMetadata Metadata(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AuditMetadata>(json, AuditSerialization.Options) ?? new();
            return new(value.SessionId, value.Method is { } method && Enum.IsDefined(method) ? method : null, value.Count >= 0 ? value.Count : null);
        }
        catch (JsonException) { return new(); }
    }
}
