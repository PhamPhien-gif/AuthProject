using System.Buffers.Binary;
using AuthProject.Entities;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class PasswordHashServiceTests
{
    private static PasswordHashService Create() => new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = 210_000
    }));

    [Fact]
    public void Hash_WhenSamePasswordUsed_UsesSaltAndConfiguredVersionedWorkFactor()
    {
        var service = Create();
        var user = new User();
        const string password = "  Unicode mật khẩu  ";
        var first = service.Hash(user, password);
        Assert.NotEqual(password, first);
        Assert.NotEqual(first, service.Hash(user, password));
        var bytes = Convert.FromBase64String(first);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1))); // HMAC-SHA512
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)));
        Assert.Equal(PasswordCheck.Success, service.Verify(user, first, password));
        Assert.Equal(PasswordCheck.Failed, service.Verify(user, first, password.Trim()));
    }

    [Fact]
    public void Verify_WhenHashHasLowerWorkFactor_RequiresRehashAndNewHashVerifies()
    {
        var user = new User();
        const string password = "a long legacy password";
        var oldHash = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }))
            .HashPassword(user, password);
        var service = Create();
        Assert.Equal(PasswordCheck.NeedsRehash, service.Verify(user, oldHash, password));
        Assert.Equal(PasswordCheck.Success, service.Verify(user, service.Hash(user, password), password));
        Assert.Equal(PasswordCheck.Failed, service.Verify(user, oldHash, "wrong"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("AQID")]
    public void Verify_WhenHashMalformed_ReturnsFailed(string hash)
        => Assert.Equal(PasswordCheck.Failed, Create().Verify(new User(), hash, "password"));

    [Fact]
    public void Verify_WhenHashHasStrongerWorkFactor_DoesNotRequestDowngrade()
    {
        var user = new User();
        const string password = "a stronger existing password";
        var stronger = new PasswordHasher<User>(Options.Create(new PasswordHasherOptions { IterationCount = 300_000 }));
        Assert.Equal(PasswordCheck.Success, Create().Verify(user, stronger.HashPassword(user, password), password));
    }
}
