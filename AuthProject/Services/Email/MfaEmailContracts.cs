using System.Text.Json;
using AuthProject.Entities;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.Services.Email;

public sealed record MfaEmail(Guid ChallengeId, Guid UserId, string Recipient, string Code,
    int Version, long AuthVersion, ChallengePurpose Purpose, DateTimeOffset ExpiresAt);
public sealed class MfaEmailProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("AuthProject.MfaEmail.v1");
    public string Protect(MfaEmail email) => protector.Protect(JsonSerializer.Serialize(email));
    public MfaEmail Unprotect(string payload) => JsonSerializer.Deserialize<MfaEmail>(protector.Unprotect(payload))
        ?? throw new InvalidOperationException("Invalid MFA email payload.");
}
public static class MfaEmailTemplate
{
    public static EmailContent Render(MfaEmail email) => new("AuthProject: mã xác minh MFA",
        $"Mã xác minh cho {email.Purpose}: {email.Code}\nCó hiệu lực đến {email.ExpiresAt:O}. Không chia sẻ mã này. Nếu không yêu cầu, hãy bỏ qua email.");
}
