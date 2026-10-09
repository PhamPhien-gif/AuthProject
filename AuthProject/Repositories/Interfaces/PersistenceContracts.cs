using AuthProject.DTOs;
using AuthProject.Entities;

namespace AuthProject.Repositories.Interfaces;

public interface IUserRepository
{
    Task<User?> FindForAuthenticationAsync(string email, CancellationToken cancellationToken = default);
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default);
    void Add(User user);
}

public interface IRoleRepository
{
    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default);
    void Assign(Guid userId, Guid roleId);
}

public interface ISessionRepository
{
    Task<AuthSession?> FindForUpdateAsync(Guid sessionId, CancellationToken cancellationToken = default);
    void Add(AuthSession session);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindForUpdateAsync(string tokenHash, CancellationToken cancellationToken = default);
    void Add(RefreshToken token);
}

public interface IAuthUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}
