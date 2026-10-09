using System.Text.RegularExpressions;
using AuthProject.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class MigrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task Migration_WhenAppliedTwice_SeedsOnlyUserAndAdminWithoutAccounts()
    {
        await using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        Assert.NotEmpty(context.Database.GetMigrations());
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        Assert.Equal(new[] { "Admin", "User" }, await context.Roles.OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync());
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class MigrationScriptTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task IdempotentSql_WhenAppliedToEmptyDatabaseAndReplayed_PreservesSchemaAndSeeds()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var name in new[] { "001-InitialAuth.sql", "002-P04-Lockout.sql" })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", name));
            var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            for (var pass = 0; pass < 2; pass++)
                foreach (var batch in batches.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    await using var command = new SqlCommand(batch, connection) { CommandTimeout = 60 };
                    await command.ExecuteNonQueryAsync();
                }
        }
        await using var context = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
        await context.Database.MigrateAsync();
        Assert.Equal(new[] { "Admin", "User" }, await context.Roles.OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync());
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Equal(context.Database.GetMigrations().Count(), (await context.Database.GetAppliedMigrationsAsync()).Count());
    }
}
