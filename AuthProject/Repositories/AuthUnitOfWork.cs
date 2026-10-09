using AuthProject.Data;
using AuthProject.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.Repositories;

public sealed class AuthUnitOfWork(AuthDbContext context) : IAuthUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Nested authentication transactions are not supported.");
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            // Roll back even when the request cancellation token has been canceled.
            try { await transaction.RollbackAsync(CancellationToken.None); }
            finally { context.ChangeTracker.Clear(); }
            throw;
        }
    }
}
