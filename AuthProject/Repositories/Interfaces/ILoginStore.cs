using AuthProject.DTOs;
using AuthProject.Security.Interfaces;

namespace AuthProject.Repositories.Interfaces;

public interface ILoginStore
{
    Task<AuthResult?> CompleteAsync(PasswordAcceptance accepted, string password, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken);
    Task<MeResponse?> GetMeAsync(Guid userId, CancellationToken cancellationToken);
    Task<string[]?> ValidateSessionAsync(Guid userId, Guid sessionId, long authVersion, CancellationToken cancellationToken);
}
