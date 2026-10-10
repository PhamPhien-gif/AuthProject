namespace AuthProject.Services.Email;

public sealed record VerificationEmail(Guid ChallengeId, Guid UserId, string Recipient, string Token, DateTimeOffset ExpiresAt);
public sealed record EmailContent(string Subject, string TextBody);
public interface IEmailPayloadProtector
{
    string Protect(VerificationEmail email);
    VerificationEmail Unprotect(string payload);
}
public interface IEmailTransport
{
    Task SendAsync(Guid messageId, string recipient, EmailContent content, CancellationToken cancellationToken);
}
