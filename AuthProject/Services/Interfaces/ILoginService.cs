using AuthProject.DTOs;

namespace AuthProject.Services.Interfaces;

public interface ILoginService
{
    Task<AuthResult> LoginAsync(LoginRequest request, string traceId, string? ip, string? userAgent, CancellationToken cancellationToken);
    Task<MeResponse> GetMeAsync(Guid userId, CancellationToken cancellationToken);
}
