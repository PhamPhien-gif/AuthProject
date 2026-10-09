using AuthProject.Entities;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AuthProject.Security;

public sealed class PasswordHashService(IOptions<PasswordHasherOptions> options) : IPasswordHashService
{
    private readonly PasswordHasher<User> hasher = new(options);

    public string Hash(User user, string password) => hasher.HashPassword(user, password);

    public PasswordCheck Verify(User user, string hash, string password)
    {
        try
        {
            return hasher.VerifyHashedPassword(user, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheck.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.NeedsRehash,
                _ => PasswordCheck.Failed
            };
        }
        catch (FormatException) { return PasswordCheck.Failed; }
    }
}
