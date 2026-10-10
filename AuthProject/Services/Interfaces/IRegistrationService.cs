using AuthProject.DTOs;

namespace AuthProject.Services.Interfaces;

public interface IRegistrationService
{
    Task RegisterAsync(RegisterRequest request, string traceId, CancellationToken cancellationToken = default);
    Task ResendAsync(EmailRequest request, string traceId, CancellationToken cancellationToken = default);
    Task VerifyAsync(VerifyEmailRequest request, string traceId, CancellationToken cancellationToken = default);
}
