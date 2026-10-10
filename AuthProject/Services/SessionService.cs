using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed class SessionService(Lazy<ISessionStore> store, IOptions<RefreshOptions> options)
{
    // Do not resolve persistence/signing dependencies while the module is disabled.
    private ISessionStore Store => store.Value;
    public void RequireEnabled() { if (!options.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE"); }
    public Task<RefreshAttempt> RefreshAsync(string? token, string trace, CancellationToken ct)
    {
        RequireEnabled();
        var digest = RefreshTokenSecret.Digest(token);
        return digest is null ? Task.FromResult(new RefreshAttempt()) : Database(() => Store.RefreshAsync(digest, trace, ct));
    }
    public async Task LogoutAsync(string? token, string trace, CancellationToken ct)
    {
        RequireEnabled();
        if (RefreshTokenSecret.Digest(token) is { } digest) await Database(async () => { await Store.LogoutAsync(digest, trace, ct); return true; });
    }
    public Task LogoutAllAsync(Guid actor, string trace, CancellationToken ct)
    {
        RequireEnabled();
        return Database(async () => { await Store.LogoutAllAsync(actor, trace, ct); return true; });
    }
    public async Task RevokeAsync(Guid actor, Guid id, string trace, CancellationToken ct)
    {
        RequireEnabled();
        if (id == Guid.Empty) throw new AppException("VALIDATION_FAILED");
        if (!await Database(() => Store.RevokeOwnedAsync(actor, id, trace, ct))) throw new AppException("RESOURCE_NOT_FOUND");
    }
    public Task<PagedResponse<SessionResponse>> ListAsync(Guid actor, Guid current, PagingRequest paging, CancellationToken ct)
    {
        RequireEnabled();
        if (paging.Page < 1 || paging.PageSize is < 1 or > 100) throw new AppException("VALIDATION_FAILED");
        return Database(() => Store.ListAsync(actor, current, paging, ct));
    }
    private static async Task<T> Database<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
}
