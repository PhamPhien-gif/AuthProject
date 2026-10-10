using System.Data.Common;
using AuthProject.Configuration;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Repositories;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.Extensions.Options;

namespace AuthProject.Services;

public sealed record MfaLoginResult(SessionGrant? Session = null, MfaChallengeGrant? Challenge = null);
public sealed class MfaService(Lazy<MfaStore> store, Lazy<IPasswordCredentialGate> gate, Lazy<ILoginStore> login,
    Lazy<IPasswordRecoveryStore> recovery, IOptions<EmailOtpOptions> options)
{
    public bool Enabled => options.Value.Enabled;
    private void RequireEnabled() { if (!Enabled) throw new AppException("SERVICE_UNAVAILABLE"); }
    public Task<MfaLoginResult> LoginAsync(LoginRequest request, string trace, string? ip, string? agent, CancellationToken ct) => Database(async () =>
    {
        RequireEnabled();
        var accepted = await gate.Value.CheckAsync(request.Email!, request.Password!, trace, ct);
        var challenge = await store.Value.BeginAsync(accepted.UserId, ChallengePurpose.LoginMfa, null, accepted, request.Password, trace, ct);
        if (challenge is not null) return new MfaLoginResult(Challenge: challenge);
        var session = await login.Value.CompleteAsync(accepted, request.Password!, trace, ip, agent, ct)
            ?? throw new AppException("AUTH_INVALID_CREDENTIALS");
        return new MfaLoginResult(Session: session);
    });
    public Task<MfaChallengeGrant> EnrollAsync(PasswordActor actor, string trace, CancellationToken ct) => Database(async () =>
    {
        RequireEnabled();
        return (await store.Value.BeginAsync(actor.UserId, ChallengePurpose.EnrollMfa, actor, null, null, trace, ct))!;
    });
    public Task<MfaChallengeGrant> StepUpAsync(PasswordActor actor, ReauthenticateRequest request, string trace, CancellationToken ct) => Database(async () =>
    {
        RequireEnabled();
        var email = await recovery.Value.GetActorEmailAsync(actor, ct) ?? throw new AppException("AUTH_SESSION_INVALID");
        var accepted = await gate.Value.CheckAsync(email, request.Password!, trace, ct);
        if (accepted.UserId != actor.UserId) throw new AppException("AUTH_SESSION_INVALID");
        return (await store.Value.BeginAsync(actor.UserId, ChallengePurpose.StepUp, actor, accepted, request.Password, trace, ct))!;
    });
    public Task<MfaChallengeGrant> ResendAsync(MfaTicket ticket, PasswordActor? actor, string trace, CancellationToken ct) => Database(() =>
    { RequireEnabled(); return store.Value.ResendAsync(ticket, actor, trace, ct); });
    public Task<MfaVerification> VerifyAsync(MfaTicket ticket, PasswordActor? actor, string code, bool isRecovery, string trace, CancellationToken ct) => Database(() =>
    { RequireEnabled(); return store.Value.VerifyAsync(ticket, actor, code, isRecovery, trace, ct); });
    public Task<RecoveryCodesResponse?> ChangeAsync(PasswordActor actor, bool disable, string trace, CancellationToken ct) => Database(() =>
    { RequireEnabled(); return store.Value.ChangeAsync(actor, disable, trace, ct); });
    private static async Task<T> Database<T>(Func<Task<T>> action)
    { try { return await action(); } catch (DbException) { throw new AppException("SERVICE_UNAVAILABLE"); } }
}
