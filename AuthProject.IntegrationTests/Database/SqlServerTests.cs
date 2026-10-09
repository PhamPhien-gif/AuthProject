using Microsoft.Data.SqlClient;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration")]
[Trait("Dependency", "SqlServer")]
public sealed class SqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture fixture;
    public SqlServerTests(SqlServerFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Database_WhenFixtureStarts_UsesFreshTestCatalogAndSupportsRollback()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var nameQuery = new SqlCommand("SELECT DB_NAME()", connection);
        Assert.Equal(fixture.DatabaseName, await nameQuery.ExecuteScalarAsync());
        Assert.Matches("^AuthProject_Test_[a-f0-9]{32}$", fixture.DatabaseName);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var create = new SqlCommand("CREATE TABLE dbo.HarnessProbe (Id int NOT NULL PRIMARY KEY)", connection, transaction))
            await create.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
        await using var probe = new SqlCommand("SELECT OBJECT_ID(N'dbo.HarnessProbe', N'U')", connection);
        Assert.Equal(DBNull.Value, await probe.ExecuteScalarAsync());
    }
}
