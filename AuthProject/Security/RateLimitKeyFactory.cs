using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AuthProject.Security;

public sealed class RateLimitKeyFactory
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public string ForAccount(string email)
    {
        if (email.Trim().Length > 254) throw new ArgumentException("Account identifier is too long.", nameof(email));
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(EmailNormalizer.Normalize(email))));
    }
    public string ForIp(IPAddress? address) => address is null ? "unknown" :
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    public string ForSession(Guid id) => Convert.ToHexString(HMACSHA256.HashData(key, id.ToByteArray()));
}
