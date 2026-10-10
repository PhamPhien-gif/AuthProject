namespace AuthProject.Repositories.Interfaces;

public interface IRegistrationStore
{
    Task RegisterAsync(string email, string displayName, string passwordHash, string traceId, CancellationToken cancellationToken);
    Task ResendAsync(string email, string traceId, CancellationToken cancellationToken);
    Task<bool> VerifyAsync(string digest, string traceId, CancellationToken cancellationToken);
}
