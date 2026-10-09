using AuthProject.Configuration;
using AuthProject.DTOs.Validators;
using AuthProject.Entities;
using AuthProject.Exceptions;
using AuthProject.Security;
using AuthProject.Security.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LockoutOptions = AuthProject.Configuration.LockoutOptions;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class PasswordCredentialGateTests
{
    private sealed class Store(User? user) : IPasswordAttemptStore
    {
        public Task<User?> FindAsync(string email, CancellationToken ct = default) => Task.FromResult(user);
        public Task<PasswordAcceptance?> CompleteAsync(User? snapshot, PasswordCheck check, string trace, CancellationToken ct = default) =>
            Task.FromResult(snapshot is not null && snapshot.PasswordHash is not null
                && LockoutRules.Apply(snapshot, check != PasswordCheck.Failed, DateTimeOffset.UtcNow, new LockoutOptions()).Accepted
                ? new PasswordAcceptance(snapshot.Id, snapshot.AuthVersion, check == PasswordCheck.NeedsRehash) : null);
    }

    private sealed class Hasher : IPasswordHashService
    {
        public int Calls;
        public string? UsedHash;
        public string Hash(User user, string password) => throw new InvalidOperationException("Gate must not rehash before P06 commits authentication.");
        public PasswordCheck Verify(User user, string hash, string password)
        {
            Calls++;
            UsedHash = hash;
            return password == "correct" ? PasswordCheck.Success : password == "rehash" ? PasswordCheck.NeedsRehash : PasswordCheck.Failed;
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong")]
    [InlineData("locked")]
    [InlineData("disabled")]
    [InlineData("unverified")]
    [InlineData("google-only")]
    public async Task RejectedAccounts_AlwaysVerifyAndReturnSamePublicFailure(string kind)
    {
        var user = kind == "unknown" ? null : new User { PasswordHash = kind == "google-only" ? null : "stored-hash",
            Status = kind == "disabled" ? UserStatus.Disabled : UserStatus.Active,
            EmailConfirmedAt = kind == "unverified" ? null : DateTimeOffset.UtcNow,
            LockoutEnd = kind == "locked" ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
        var hasher = new Hasher();
        var dummy = new DummyPasswordHash(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
        var gate = new PasswordCredentialGate(new Store(user), hasher, dummy, new LoginRequestValidator());
        var error = await Assert.ThrowsAsync<AppException>(() => gate.CheckAsync("a@example.test", kind == "wrong" ? "wrong" : "correct", "trace_123"));
        Assert.Equal(ApiErrors.Get("AUTH_INVALID_CREDENTIALS"), error.Error);
        Assert.Equal(1, hasher.Calls);
        Assert.Equal(kind is "unknown" or "google-only" ? dummy.Hash : "stored-hash", hasher.UsedHash);
    }

    [Fact]
    public async Task CorrectPrimaryFactor_ReturnsOnlyIdentityAndRehashRequirement()
    {
        var user = new User { PasswordHash = "legacy-hash", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };
        var gate = new PasswordCredentialGate(new Store(user), new Hasher(), new DummyPasswordHash(Options.Create(new PasswordHasherOptions())), new LoginRequestValidator());
        var result = await gate.CheckAsync("a@example.test", "rehash", "trace_123");
        Assert.Equal(user.Id, result.UserId);
        Assert.True(result.RequiresRehash);
    }
}
