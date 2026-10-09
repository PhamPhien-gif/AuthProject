using AuthProject.Data;
using AuthProject.DTOs;
using AuthProject.Entities;
using AuthProject.Repositories.Interfaces;
using AuthProject.Security;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.Repositories;

public sealed class UserRepository(AuthDbContext context) : IUserRepository
{
    public Task<User?> FindForAuthenticationAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = EmailNormalizer.Normalize(email);
        // Tracked only for authentication/rehash and state mutations within the unit of work.
        return context.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
    }

    public Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default) =>
        context.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new UserProfile(x.Id, x.Email, x.DisplayName, x.EmailConfirmedAt != null, x.Status, x.MfaEmailEnabled,
                (from ur in context.UserRoles
                 join role in context.Roles on ur.RoleId equals role.Id
                 where ur.UserId == x.Id
                 orderby role.Name
                 select role.Name).ToArray()))
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(User user) => context.Users.Add(user);
}

public sealed class RoleRepository(AuthDbContext context) : IRoleRepository
{
    public Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = name.Trim().ToUpperInvariant();
        return context.Roles.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedName == normalized, cancellationToken);
    }
    public void Assign(Guid userId, Guid roleId) => context.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
}

public sealed class SessionRepository(AuthDbContext context) : ISessionRepository
{
    public Task<AuthSession?> FindForUpdateAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        context.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
    public void Add(AuthSession session) => context.Sessions.Add(session);
}

public sealed class RefreshTokenRepository(AuthDbContext context) : IRefreshTokenRepository
{
    public Task<RefreshToken?> FindForUpdateAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var canonical = tokenHash.ToUpperInvariant();
        return context.RefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == canonical, cancellationToken);
    }
    public void Add(RefreshToken token) => context.RefreshTokens.Add(token);
}
