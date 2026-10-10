using AuthProject.Security;
using AuthProject.Security.Interfaces;

namespace AuthProject.Repositories.Interfaces;

public enum PasswordMutationResult { Success, InvalidSession, StepUpRequired }
public interface IPasswordRecoveryStore
{
    Task ForgotAsync(string email, string trace, CancellationToken ct);
    Task<bool> ResetAsync(string digest, string newPassword, string trace, CancellationToken ct);
    Task<string?> GetActorEmailAsync(PasswordActor actor, CancellationToken ct);
    Task<PasswordMutationResult> ChangeAsync(PasswordActor actor, PasswordAcceptance accepted, string newPassword, string trace, CancellationToken ct);
    Task<PasswordMutationResult> ReauthenticateAsync(PasswordActor actor, PasswordAcceptance accepted, string trace, CancellationToken ct);
}
