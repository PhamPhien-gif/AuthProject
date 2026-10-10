using AuthProject.Entities;

namespace AuthProject.Security;

public static class AdminAccessRules
{
    public static string? SessionError(User? user, AuthSession? session, PasswordActor actor, DateTimeOffset now)
    {
        if (user is null || session is null || user.Id != actor.UserId || session.UserId != user.Id || session.Id != actor.SessionId
            || user.Status != UserStatus.Active || user.EmailConfirmedAt is null || user.LockoutEnd > now
            || user.AuthVersion != actor.AuthVersion || session.AuthVersion != user.AuthVersion || session.RevokedAt != null
            || session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now)
            return "AUTH_SESSION_INVALID";
        return null;
    }
    public static string? Error(User? user, AuthSession? session, PasswordActor actor, bool isAdmin,
        DateTimeOffset now, int seconds, bool recent)
    {
        if (SessionError(user, session, actor, now) is { } error) return error;
        if (!isAdmin) return "AUTH_FORBIDDEN";
        if (!user!.MfaEmailEnabled || session!.MfaVerifiedAt is null || session.MfaVerifiedAt > now) return "AUTH_MFA_REQUIRED";
        if (recent && (!RecentAuthenticationRules.Primary(session, now, seconds) || !RecentAuthenticationRules.Mfa(session, now, seconds)))
            return "AUTH_STEP_UP_REQUIRED";
        return null;
    }
}
