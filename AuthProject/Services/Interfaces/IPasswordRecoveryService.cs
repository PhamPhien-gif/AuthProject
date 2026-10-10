using AuthProject.DTOs;
using AuthProject.Security;

namespace AuthProject.Services.Interfaces;

public interface IPasswordRecoveryService
{
    Task ForgotAsync(EmailRequest request, string trace, CancellationToken ct);
    Task ResetAsync(ResetPasswordRequest request, string trace, CancellationToken ct);
    Task ChangeAsync(PasswordActor actor, ChangePasswordRequest request, string trace, CancellationToken ct);
    Task ReauthenticateAsync(PasswordActor actor, ReauthenticateRequest request, string trace, CancellationToken ct);
}
