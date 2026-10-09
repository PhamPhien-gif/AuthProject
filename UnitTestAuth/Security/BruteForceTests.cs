using System.Net;
using AuthProject.Configuration;
using AuthProject.Entities;
using AuthProject.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class BruteForceTests
{
    [Fact]
    public void RateStore_EnforcesBurstAndReleasesPermitsAtExactWindowBoundary()
    {
        var clock = new FakeTimeProvider();
        var store = new BoundedRateLimitStore(clock, Options.Create(new BruteForceOptions()));
        RateLimitDemand[] demand = [new("key", 5, TimeSpan.FromMinutes(1))];
        var allowed = 0;
        Parallel.For(0, 40, _ => { if (store.Acquire(demand).Allowed) Interlocked.Increment(ref allowed); });
        Assert.Equal(5, allowed);
        Assert.Equal(60, store.Acquire(demand).RetryAfterSeconds);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(store.Acquire(demand).Allowed);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(store.Acquire(demand).Allowed);
    }

    [Fact]
    public void RateStore_WhenCapacityFull_RejectsNewPartitionsWithoutEvictingLiveCounters()
    {
        var clock = new FakeTimeProvider();
        var store = new BoundedRateLimitStore(clock, Options.Create(new BruteForceOptions { MaxPartitions = 2 }));
        RateLimitDecision Take(string key) => store.Acquire([new(key, 1, TimeSpan.FromMinutes(1))]);
        Assert.True(Take("first").Allowed);
        Assert.True(Take("second").Allowed);
        Assert.False(Take("third").Allowed);
        Assert.False(Take("first").Allowed);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(Take("third").Allowed);
    }

    [Fact]
    public void RateKeys_NormalizeIdentityAndKeepPiiOutOfBoundedKeys()
    {
        var factory = new RateLimitKeyFactory();
        var account = factory.ForAccount("  Person@example.test ");
        Assert.Equal(account, factory.ForAccount("PERSON@EXAMPLE.TEST"));
        Assert.NotEqual(account, new RateLimitKeyFactory().ForAccount("Person@example.test"));
        Assert.Equal(64, account.Length);
        Assert.DoesNotContain("PERSON", account);
        Assert.Equal(factory.ForIp(IPAddress.Parse("192.0.2.1")), factory.ForIp(IPAddress.Parse("::ffff:192.0.2.1")));
    }

    private static User Active() => new() { Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Lockout_StopsCounterAtThresholdAndDoesNotExtendCooldownOnBlockedAttempts()
    {
        var clock = new FakeTimeProvider();
        var user = Active();
        var options = new LockoutOptions();
        for (var i = 0; i < 5; i++) Assert.False(LockoutRules.Apply(user, false, clock.GetUtcNow(), options).Accepted);
        var end = user.LockoutEnd;
        Assert.Equal(clock.GetUtcNow().AddSeconds(900), end);
        for (var i = 0; i < 20; i++) Assert.Equal(AuditReason.Locked, LockoutRules.Apply(user, true, clock.GetUtcNow(), options).Reason);
        Assert.Equal(5, user.FailedLoginCount);
        Assert.Equal(end, user.LockoutEnd);
        clock.Advance(TimeSpan.FromSeconds(899));
        Assert.False(LockoutRules.Apply(user, true, clock.GetUtcNow(), options).Accepted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(LockoutRules.Apply(user, true, clock.GetUtcNow(), options).Accepted);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEnd);
        Assert.Null(user.FailedLoginWindowStart);
    }

    [Fact]
    public void Lockout_ResetsOldFailureWindowAndSuccessfulAttempt()
    {
        var clock = new FakeTimeProvider();
        var user = Active();
        var options = new LockoutOptions();
        LockoutRules.Apply(user, false, clock.GetUtcNow(), options);
        clock.Advance(TimeSpan.FromSeconds(900));
        LockoutRules.Apply(user, false, clock.GetUtcNow(), options);
        Assert.Equal(1, user.FailedLoginCount);
        Assert.Null(user.LockoutEnd);
        Assert.True(LockoutRules.Apply(user, true, clock.GetUtcNow(), options).Accepted);
        Assert.Equal(0, user.FailedLoginCount);
    }
}
