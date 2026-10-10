using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Exceptions;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using AuthProject.Services.Interfaces;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed class PasswordRecoveryService(Lazy<IPasswordRecoveryStore> store, Lazy<IPasswordCredentialGate> gate,
    IOptions<PasswordRecoveryOptions> options, IValidator<EmailRequest> forgotValidator, IValidator<ResetPasswordRequest> resetValidator,
    IValidator<ChangePasswordRequest> changeValidator, IValidator<ReauthenticateRequest> reauthValidator) : IPasswordRecoveryService
{
    private void Enabled() { if (!options.Value.Enabled) throw new AppException("SERVICE_UNAVAILABLE"); }
    public async Task ForgotAsync(EmailRequest request, string trace, CancellationToken ct)
    {
        Enabled(); await Validate(forgotValidator, request, ct);
        await Database(() => store.Value.ForgotAsync(request.Email!.Trim(), trace, ct));
    }
    public async Task ResetAsync(ResetPasswordRequest request, string trace, CancellationToken ct)
    {
        Enabled(); await Validate(resetValidator, request, ct);
        var digest = EmailVerificationToken.Digest(request.Token);
        if (digest is null || !await Database(() => store.Value.ResetAsync(digest, request.NewPassword!, trace, ct))) throw new AppException("AUTH_CHALLENGE_INVALID");
    }
    public async Task ChangeAsync(PasswordActor actor, ChangePasswordRequest request, string trace, CancellationToken ct)
    {
        Enabled(); await Validate(changeValidator, request, ct);
        var accepted = await Accept(actor, request.CurrentPassword!, trace, ct);
        Result(await Database(() => store.Value.ChangeAsync(actor, accepted, request.NewPassword!, trace, ct)));
    }
    public async Task ReauthenticateAsync(PasswordActor actor, ReauthenticateRequest request, string trace, CancellationToken ct)
    {
        Enabled(); await Validate(reauthValidator, request, ct);
        var accepted = await Accept(actor, request.Password!, trace, ct);
        Result(await Database(() => store.Value.ReauthenticateAsync(actor, accepted, trace, ct)));
    }
    private async Task<PasswordAcceptance> Accept(PasswordActor actor, string password, string trace, CancellationToken ct)
    {
        var email = await Database(() => store.Value.GetActorEmailAsync(actor, ct)) ?? throw new AppException("AUTH_SESSION_INVALID");
        var accepted = await Database(() => gate.Value.CheckAsync(email, password, trace, ct));
        if (accepted.UserId != actor.UserId) throw new AppException("AUTH_SESSION_INVALID");
        return accepted;
    }
    private static void Result(PasswordMutationResult result)
    {
        if (result == PasswordMutationResult.StepUpRequired) throw new AppException("AUTH_STEP_UP_REQUIRED");
        if (result != PasswordMutationResult.Success) throw new AppException("AUTH_SESSION_INVALID");
    }
    private static async Task Validate<T>(IValidator<T> validator, T value, CancellationToken ct)
    {
        if (!(await validator.ValidateAsync(value, ct)).IsValid) throw new AppException("VALIDATION_FAILED");
    }
    private static async Task<T> Database<T>(Func<Task<T>> action)
    {
        try { return await action(); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
    private static async Task Database(Func<Task> action)
    {
        try { await action(); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); }
    }
}
