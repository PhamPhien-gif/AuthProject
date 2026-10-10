using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace AuthProject.Security;

public static class RefreshTokenSecret
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static string? Digest(string? token)
    {
        if (token is not { Length: 43 } || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            return bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == token ? Convert.ToHexString(SHA256.HashData(bytes)) : null;
        }
        catch (FormatException) { return null; }
    }
}
