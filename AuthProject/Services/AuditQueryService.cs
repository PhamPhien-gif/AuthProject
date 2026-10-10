using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Security;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed class AuditQueryService(Lazy<AuditQueryStore> store, AdminService admin, IOptions<PersistenceOptions> options)
{
    public Task<PagedResponse<LoginHistoryResponse>> HistoryAsync(PasswordActor actor, HistoryQuery paging, CancellationToken ct) => Database(() => store.Value.HistoryAsync(actor, paging, ct));
    public async Task<PagedResponse<AuditResponse>> AuditAsync(PasswordActor actor, AuditQuery paging, CancellationToken ct)
    { await admin.AuthorizeAsync(actor, false, ct); return await Database(() => store.Value.AuditAsync(paging, ct)); }
    private async Task<T> Database<T>(Func<Task<T>> action)
    {
        if (!options.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE");
        try { return await action(); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
}
