using AuthProject.Configuration;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Logging;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Repositories;

public sealed record BootstrapResult(Guid UserId, bool Created);

public sealed class AdminStore(IDbContextFactory<AuthDbContext> factory, IAuditPayloadProtector audits, TimeProvider clock, IOptions<EmailOtpOptions> otp)
{
    public async Task AuthorizeAsync(PasswordActor actor, bool recent, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await RequireAdmin(db, actor, recent, ct);
    }
    private async Task RequireAdmin(AuthDbContext db, PasswordActor actor, bool recent, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actor.UserId, ct);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == actor.SessionId, ct);
        var role = await db.UserRoles.AnyAsync(r => r.UserId == actor.UserId && r.RoleId == SystemRoles.AdminId, ct);
        if (AdminAccessRules.Error(user, session, actor, role, clock.GetUtcNow(), otp.Value.RecentAuthenticationSeconds, recent) is { } error)
            throw new AppException(error);
    }
    public async Task<PagedResponse<AdminUserResponse>> ListUsersAsync(PasswordActor actor, AdminUserQuery paging, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await RequireAdmin(db, actor, false, ct);
        var query = db.Users.AsNoTracking();
        if (paging.Status is { } status) { var value = Enum.Parse<UserStatus>(status); query = query.Where(u => u.Status == value); }
        if (!string.IsNullOrWhiteSpace(paging.Search))
        { var search = paging.Search.Trim(); query = query.Where(u => u.Email.Contains(search) || u.DisplayName.Contains(search)); }
        var total = await query.CountAsync(ct); var offset = (long)(paging.Page - 1) * paging.PageSize;
        var items = offset > int.MaxValue ? [] : await query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).Skip((int)offset).Take(paging.PageSize)
            .Select(u => new AdminUserResponse(u.Id, u.Email, u.DisplayName, u.Status.ToString(), u.MfaEmailEnabled, u.CreatedAt,
                (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == u.Id orderby r.Name select r.Name).ToArray())).ToArrayAsync(ct);
        return new(items, paging.Page, paging.PageSize, total);
    }
    public async Task<RoleResponse[]> ListRolesAsync(PasswordActor actor, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await RequireAdmin(db, actor, false, ct);
        return await db.Roles.AsNoTracking().Where(r => r.Id == SystemRoles.UserId || r.Id == SystemRoles.AdminId)
            .OrderBy(r => r.Name).Select(r => new RoleResponse(r.Id, r.Name, r.IsSystem)).ToArrayAsync(ct);
    }
    public Task SetRolesAsync(PasswordActor actor, Guid target, SetUserRolesRequest request, string trace, CancellationToken ct)
    {
        var names = request.RoleNames;
        if (names is null || names.Length is < 1 or > 10 || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(n => n is not (SystemRoles.User or SystemRoles.Admin))) throw new AppException("VALIDATION_FAILED");
        var ids = names.Select(n => n == SystemRoles.User ? SystemRoles.UserId : SystemRoles.AdminId).Order().ToArray();
        return Mutate(actor, target, ids, null, trace, ct);
    }
    public Task SetStatusAsync(PasswordActor actor, Guid target, ChangeUserStatusRequest request, string trace, CancellationToken ct)
    {
        if (request.Status is not ("Active" or "Disabled")) throw new AppException("VALIDATION_FAILED");
        return Mutate(actor, target, null, Enum.Parse<UserStatus>(request.Status), trace, ct);
    }
    private async Task Mutate(PasswordActor actor, Guid target, Guid[]? roles, UserStatus? status, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GovernanceLock(db, ct);
        // Global governance lock first, then user locks in stable order; recheck actor inside the transaction.
        foreach (var id in new[] { actor.UserId, target }.Distinct().Order()) await LockUser(db, id, ct);
        await RequireAdmin(db, actor, true, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == target, ct) ?? throw new AppException("RESOURCE_NOT_FOUND");
        var existing = await db.UserRoles.Where(r => r.UserId == target).ToArrayAsync(ct);
        var removingAdmin = existing.Any(r => r.RoleId == SystemRoles.AdminId) && user.Status == UserStatus.Active && user.EmailConfirmedAt != null
            && (status == UserStatus.Disabled || roles is not null && !roles.Contains(SystemRoles.AdminId));
        if (removingAdmin && await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId
            where u.Status == UserStatus.Active && u.EmailConfirmedAt != null && ur.RoleId == SystemRoles.AdminId select u.Id).CountAsync(ct) <= 1)
            throw new AppException("AUTH_LAST_ADMIN");
        if (status == UserStatus.Active && user.EmailConfirmedAt is null) throw new AppException("RESOURCE_CONFLICT");
        var unchanged = roles is not null ? existing.Select(r => r.RoleId).Order().SequenceEqual(roles) : user.Status == status;
        if (unchanged) { await tx.CommitAsync(ct); return; }
        if (target == actor.UserId) throw new AppException("AUTH_FORBIDDEN");
        if (roles is not null)
        {
            if (await db.Roles.CountAsync(r => roles.Contains(r.Id), ct) != roles.Length) throw new AppException("RESOURCE_CONFLICT");
            db.UserRoles.RemoveRange(existing.Where(r => !roles.Contains(r.RoleId)));
            db.UserRoles.AddRange(roles.Where(id => !existing.Any(r => r.RoleId == id)).Select(id => new UserRole { UserId = target, RoleId = id }));
        }
        else user.Status = status!.Value;
        await Revoke(db, user, roles is not null ? "RolesChanged" : "UserStatusChanged", actor.UserId, trace, ct);
        Audit(db, roles is not null ? AuditEventType.RoleChanged : AuditEventType.UserStatusChanged, actor.UserId, target, trace);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task UpdateProfileAsync(PasswordActor actor, UpdateProfileRequest request, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockUser(db, actor.UserId, ct);
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == actor.SessionId, ct);
        if (AdminAccessRules.SessionError(user, session, actor, clock.GetUtcNow()) is { } error) throw new AppException(error);
        if (user!.MfaEmailEnabled && session!.MfaVerifiedAt is null) throw new AppException("AUTH_SESSION_INVALID");
        user.DisplayName = request.DisplayName!.Trim(); Audit(db, AuditEventType.ProfileChanged, user.Id, user.Id, trace);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<BootstrapResult> BootstrapAsync(RegisterRequest request, IPasswordHashService passwords, string trace, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GovernanceLock(db, ct);
        var email = EmailNormalizer.Normalize(request.Email!);
        // Range lock also serializes with public registration; never adopt an existing non-admin account.
        var existing = await db.Users.FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, HOLDLOCK) WHERE [NormalizedEmail] = {email}").SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.Status != UserStatus.Active || existing.EmailConfirmedAt is null
                || !await db.UserRoles.AnyAsync(r => r.UserId == existing.Id && r.RoleId == SystemRoles.AdminId, ct)) throw new AppException("RESOURCE_CONFLICT");
            await tx.CommitAsync(ct); return new(existing.Id, false);
        }
        var user = new User { Email = request.Email!, DisplayName = request.DisplayName!, Status = UserStatus.Active, EmailConfirmedAt = clock.GetUtcNow() };
        user.PasswordHash = passwords.Hash(user, request.Password!); db.Users.Add(user);
        db.UserRoles.Add(new() { UserId = user.Id, RoleId = SystemRoles.UserId }); db.UserRoles.Add(new() { UserId = user.Id, RoleId = SystemRoles.AdminId });
        Audit(db, AuditEventType.AdminBootstrapped, null, user.Id, trace);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(user.Id, true);
    }
    private async Task Revoke(AuthDbContext db, User user, string reason, Guid actor, string trace, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); user.AuthVersion++;
        var sessionIds = db.Sessions.Where(s => s.UserId == user.Id).Select(s => s.Id);
        var active = await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).Select(s => s.Id).ToArrayAsync(ct);
        await db.RefreshTokens.Where(t => sessionIds.Contains(t.SessionId) && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.Sessions.Where(s => s.UserId == user.Id && s.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokeReason, reason), ct);
        await db.Challenges.Where(c => c.UserId == user.Id && c.ConsumedAt == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        foreach (var id in active) new AuditWriter(db, audits, clock).Stage(new(AuditEventType.SessionRevoked, AuditOutcome.Success, AuditReason.Revoked, trace, actor, user.Id, new(SessionId: id)));
    }
    private static async Task GovernanceLock(AuthDbContext db, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = 'AuthProject.AdminGovernance.v1', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; IF @result < 0 THROW 51000, 'Governance lock unavailable.', 1;", ct);
    private static Task<User?> LockUser(AuthDbContext db, Guid id, CancellationToken ct) => db.Users
        .FromSqlInterpolated($"SELECT * FROM [Users] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
    private void Audit(AuthDbContext db, AuditEventType type, Guid? actor, Guid subject, string trace) =>
        new AuditWriter(db, audits, clock).Stage(new(type, AuditOutcome.Success, AuditReason.None, trace, actor, subject));
}
