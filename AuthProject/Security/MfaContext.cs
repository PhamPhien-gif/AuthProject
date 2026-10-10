using System.Security.Cryptography;
using System.Text.Json;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Exceptions;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.Security;

// Protected cookie is an operation binding, never an authentication/session credential.
public sealed record MfaTicket(Guid ChallengeId, Guid UserId, Guid ContextId, ChallengePurpose Purpose,
    long AuthVersion, Guid? SessionId);
public sealed record MfaChallengeGrant(MfaChallengeResponse Response, MfaTicket Ticket);
public sealed class MfaContext(IDataProtectionProvider provider)
{
    public const string CookieName = "__Secure-AuthProject.Mfa";
    private const string Path = "/api/v1/auth";
    private readonly IDataProtector protector = provider.CreateProtector("AuthProject.MfaContext.v1");
    public void Write(HttpResponse response, MfaChallengeGrant grant) => response.Cookies.Append(CookieName,
        protector.Protect(JsonSerializer.Serialize(grant.Ticket)), Options());
    public MfaTicket Read(HttpRequest request, Guid challengeId)
    {
        var cookie = request.Cookies[CookieName];
        if (cookie is null || cookie.Length > 4096) throw new AppException("AUTH_CHALLENGE_INVALID");
        try
        {
            var ticket = JsonSerializer.Deserialize<MfaTicket>(protector.Unprotect(cookie));
            if (ticket is null || ticket.ChallengeId != challengeId) throw new AppException("AUTH_CHALLENGE_INVALID");
            return ticket;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        { throw new AppException("AUTH_CHALLENGE_INVALID"); }
    }
    public void Clear(HttpResponse response) => response.Cookies.Delete(CookieName, Options());
    private static CookieOptions Options() => new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict,
        Path = Path, IsEssential = true };
}
