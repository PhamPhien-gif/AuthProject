using AuthProject.Entities;

namespace AuthProject.Security.Interfaces;

public sealed record PasswordAcceptance(Guid UserId, long AuthVersion, bool RequiresRehash);

public interface IPasswordAttemptStore
{
    Task<User?> FindAsync(string email, CancellationToken cancellationToken = default);
    Task<PasswordAcceptance?> CompleteAsync(User? snapshot, PasswordCheck check, string traceId, CancellationToken cancellationToken = default);
}

public interface IPasswordCredentialGate
{
    Task<PasswordAcceptance> CheckAsync(string email, string password, string traceId, CancellationToken cancellationToken = default);
}
