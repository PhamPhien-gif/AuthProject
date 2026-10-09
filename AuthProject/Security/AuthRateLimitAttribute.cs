namespace AuthProject.Security;

public enum AuthRatePolicy { Api, Login, Register, Otp, Refresh, Recovery, Google }

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AuthRateLimitAttribute(AuthRatePolicy policy) : Attribute
{
    public AuthRatePolicy Policy { get; } = policy;
}
