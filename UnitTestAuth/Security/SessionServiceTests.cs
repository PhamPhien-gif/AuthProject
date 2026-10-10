using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Services;
using Microsoft.Extensions.Options;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class SessionServiceTests
{
    private sealed class Unavailable : System.Data.Common.DbException;
    private sealed class UnavailableStore : ISessionStore
    {
        public Task<RefreshAttempt> RefreshAsync(string digest, string trace, CancellationToken ct) => throw new Unavailable();
        public Task LogoutAsync(string digest, string trace, CancellationToken ct) => throw new Unavailable();
        public Task LogoutAllAsync(Guid actor, string trace, CancellationToken ct) => throw new Unavailable();
        public Task<bool> RevokeOwnedAsync(Guid actor, Guid id, string trace, CancellationToken ct) => throw new Unavailable();
        public Task<PagedResponse<SessionResponse>> ListAsync(Guid actor, Guid current, PagingRequest paging, CancellationToken ct) => throw new Unavailable();
    }
    [Fact]
    public async Task DatabaseUnavailable_FailsClosedWithReusableSafeError()
    {
        var service = new SessionService(new(() => new UnavailableStore()), Options.Create(new RefreshOptions { Enabled = true }));
        var error = await Assert.ThrowsAsync<AppException>(() => service.RefreshAsync(RefreshTokenSecret.Create(), "test", default));
        Assert.Equal("SERVICE_UNAVAILABLE", error.Error.Code);
    }
    [Fact]
    public async Task DisabledModule_DoesNotResolveDatabaseOrSigningKey()
    {
        var service = new SessionService(new(() => throw new InvalidOperationException("must not resolve")), Options.Create(new RefreshOptions()));
        var error = await Assert.ThrowsAsync<AppException>(() => service.RefreshAsync(RefreshTokenSecret.Create(), "test", default));
        Assert.Equal("SERVICE_UNAVAILABLE", error.Error.Code);
    }
    [Fact]
    public async Task MalformedCookie_DoesNotQueryDatabaseAndLogoutIsIdempotent()
    {
        var service = new SessionService(new(() => throw new InvalidOperationException("must not resolve")), Options.Create(new RefreshOptions { Enabled = true }));
        Assert.Null((await service.RefreshAsync("malformed", "test", default)).Grant);
        await service.LogoutAsync(null, "test", default);
    }
    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    public async Task InvalidPaging_IsRejectedBeforeDatabase(int page, int size)
    {
        var service = new SessionService(new(() => throw new InvalidOperationException("must not resolve")), Options.Create(new RefreshOptions { Enabled = true }));
        var error = await Assert.ThrowsAsync<AppException>(() => service.ListAsync(Guid.NewGuid(), Guid.NewGuid(), new PagingRequest { Page = page, PageSize = size }, default));
        Assert.Equal("VALIDATION_FAILED", error.Error.Code);
    }
}
