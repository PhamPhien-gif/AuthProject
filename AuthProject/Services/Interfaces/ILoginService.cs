using AuthProject.Security;
using AuthProject.DTOs;

namespace AuthProject.Services.Interfaces;

public interface ILoginService
{
    Task<SessionGrant> LoginAsync(LoginRequest request, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken);
    Task<MeResponse> GetMeAsync(Guid userId, CancellationToken cancellationToken);
}
