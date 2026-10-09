namespace AuthProject.TestSupport;

internal sealed record TestIdentity(string Subject, string Email, string DisplayName, bool EmailVerified);

// Test data only: this is not an application entity or an Auth DTO.
internal sealed class TestIdentityBuilder
{
    private string subject = Guid.NewGuid().ToString("N");
    private string email = $"test-{Guid.NewGuid():N}@example.test";
    private bool emailVerified = true;

    public TestIdentityBuilder WithSubject(string value) { subject = value; return this; }
    public TestIdentityBuilder WithEmail(string value, bool verified = true)
    {
        email = value;
        emailVerified = verified;
        return this;
    }

    public TestIdentity Build() => new(subject, email, "Test User", emailVerified);
}
