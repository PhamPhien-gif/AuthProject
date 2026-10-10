using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthProject.Configuration;
using AuthProject.Entities;
using Microsoft.Extensions.Options;

namespace AuthProject.Security;

public sealed class EmailOtpSecret(IOptions<EmailOtpOptions> options)
{
    public static string Create() => RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    public string Digest(AuthChallenge challenge, string code)
    {
        var value = FormattableString.Invariant($"AuthProject.EmailOtp.v1|{challenge.Id:N}|{challenge.UserId:N}|{challenge.ContextId:N}|{(int)challenge.Purpose}|{challenge.Version}|{challenge.AuthVersion}|{challenge.KeyId}|{code}");
        if (challenge.PrimaryMethod == AuthenticationMethod.Google) value += "|Google";
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(options.Value.Pepper), Encoding.UTF8.GetBytes(value)));
    }
    public bool Matches(AuthChallenge challenge, string? code, string digest)
    {
        if (challenge.KeyId != options.Value.KeyId || code is not { Length: 6 }
            || !code.All(char.IsAsciiDigit) || digest.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Digest(challenge, code)), Convert.FromHexString(digest)); }
        catch (FormatException) { return false; }
    }
}
