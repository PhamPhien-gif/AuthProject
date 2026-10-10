using AuthProject.Entities;

namespace AuthProject.Security;

public static class SessionLifetimeRules
{
    public static int AccessLifetime(AuthSession session, DateTimeOffset now, int configured) => (int)Math.Max(0,
        Math.Min(configured, Math.Min(session.IdleExpiresAt.ToUnixTimeSeconds(), session.AbsoluteExpiresAt.ToUnixTimeSeconds()) - now.ToUnixTimeSeconds()));
}
