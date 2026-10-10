using AuthProject.Security;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Interfaces;

namespace AuthProject.Services;

public sealed class LoginService(IPasswordCredentialGate gate, ILoginStore store) : ILoginService
{
    public async Task<SessionGrant> LoginAsync(LoginRequest request, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken)
    {
        var accepted = await gate.CheckAsync(request.Email!, request.Password!, traceId, cancellationToken);
        return await store.CompleteAsync(accepted, request.Password!, traceId, ip, userAgent, cancellationToken)
            ?? throw new AppException("AUTH_INVALID_CREDENTIALS");
    }
    public async Task<MeResponse> GetMeAsync(Guid userId, CancellationToken cancellationToken) => await store.GetMeAsync(userId, cancellationToken)
        ?? throw new AppException("AUTH_SESSION_INVALID");
}
