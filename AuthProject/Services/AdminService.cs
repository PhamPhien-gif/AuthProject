using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using FluentValidation;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed class AdminService(Lazy<AdminStore> store, IOptions<PersistenceOptions> persistence)
{
    public Task AuthorizeAsync(PasswordActor actor, bool recent, CancellationToken ct) => Database(() => store.Value.AuthorizeAsync(actor, recent, ct));
    public Task<PagedResponse<AdminUserResponse>> UsersAsync(PasswordActor actor, AdminUserQuery query, CancellationToken ct) => Database(() => store.Value.ListUsersAsync(actor, query, ct));
    public Task<RoleResponse[]> RolesAsync(PasswordActor actor, CancellationToken ct) => Database(() => store.Value.ListRolesAsync(actor, ct));
    public Task SetRolesAsync(PasswordActor actor, Guid user, SetUserRolesRequest request, string trace, CancellationToken ct) => Database(() => store.Value.SetRolesAsync(actor, user, request, trace, ct));
    public Task SetStatusAsync(PasswordActor actor, Guid user, ChangeUserStatusRequest request, string trace, CancellationToken ct) => Database(() => store.Value.SetStatusAsync(actor, user, request, trace, ct));
    public Task ProfileAsync(PasswordActor actor, UpdateProfileRequest request, string trace, CancellationToken ct) => Database(() => store.Value.UpdateProfileAsync(actor, request, trace, ct));
    public async Task<BootstrapResult> BootstrapAsync(RegisterRequest request, IValidator<RegisterRequest> validator, IPasswordHashService passwords, string trace, CancellationToken ct)
    {
        if (!(await validator.ValidateAsync(request, ct)).IsValid) throw new AppException("VALIDATION_FAILED");
        return await Database(() => store.Value.BootstrapAsync(request, passwords, trace, ct));
    }
    private async Task Database(Func<Task> action)
    { await Database(async () => { await action(); return true; }); }
    private async Task<T> Database<T>(Func<Task<T>> action)
    {
        if (!persistence.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE");
        try { return await action(); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { throw new AppException("RESOURCE_CONFLICT"); }
        catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
        catch (DbUpdateException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
}
