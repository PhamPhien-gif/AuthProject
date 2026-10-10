using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AuthProject.Exceptions;

namespace AuthProject.Security;

// Only read a principal produced by validated OIDC middleware, never request JSON.
public sealed record GoogleIdentity(string Subject, string Email, string DisplayName, long? AuthenticationTime)
{
    public static GoogleIdentity Read(ClaimsPrincipal principal)
    {
        string One(string name)
        {
            var values = principal.FindAll(name).ToArray();
            if (values.Length != 1) throw new AppException("AUTH_GOOGLE_INVALID");
            return values[0].Value;
        }
        var sub = One("sub"); var email = One("email");
        if (sub.Length is < 1 or > 255 || sub.Any(char.IsControl) || email.Length > 254
            || !new EmailAddressAttribute().IsValid(email) || One("email_verified") != "true") throw new AppException("AUTH_GOOGLE_INVALID");
        var name = principal.FindAll("name").Select(c => c.Value).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(name)) name = "Google user";
        name = name[..Math.Min(name.Length, 100)];
        var times = principal.FindAll("auth_time").ToArray();
        long? authTime = times.Length == 1 && long.TryParse(times[0].Value, out var parsed) && parsed >= 0 ? parsed : null;
        return new(sub, email.Trim(), name, authTime);
    }
}
