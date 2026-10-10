using AuthProject.Entities;

namespace AuthProject.Security;

// Constructed only from validated JWT claims, never a request DTO.
public sealed record PasswordActor(Guid UserId, Guid SessionId, long AuthVersion);
public static class RecentAuthenticationRules
{
    public static bool Primary(AuthSession session, DateTimeOffset now, int seconds) =>
        session.AuthenticatedAt <= now && session.AuthenticatedAt > now.AddSeconds(-seconds);
    public static bool Mfa(AuthSession session, DateTimeOffset now, int seconds) =>
        session.MfaVerifiedAt is { } factor && factor <= now && factor > now.AddSeconds(-seconds);
}
