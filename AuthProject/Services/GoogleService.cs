using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Security;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace AuthProject.Services;

public sealed class GoogleService(Lazy<GoogleStore> store, IOptions<GoogleOptions> options)
{
    public void RequireEnabled() { if (!options.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE"); }
    public Task<GoogleCompletionGrant> StageAsync(GoogleIdentity identity, GoogleBinding binding, string trace, CancellationToken ct) => Database(() => store.Value.StageAsync(identity, binding, trace, ct));
    public Task<MfaLoginResult> CompleteAsync(GoogleBinding binding, string ticket, PasswordActor? actor, string trace, CancellationToken ct) => Database(() => store.Value.CompleteAsync(binding, ticket, actor, trace, ct));
    public async Task AuthorizeActorAsync(PasswordActor actor, bool recent, CancellationToken ct)
    { RequireEnabled(); try { await store.Value.AuthorizeActorAsync(actor, recent, ct); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); } }
    public async Task UnlinkAsync(PasswordActor actor, string trace, CancellationToken ct)
    { RequireEnabled(); try { await store.Value.UnlinkAsync(actor, trace, ct); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); } }
    private async Task<T> Database<T>(Func<Task<T>> action)
    {
        RequireEnabled();
        try { return await action(); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { throw new AppException("RESOURCE_CONFLICT"); }
        catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
}
