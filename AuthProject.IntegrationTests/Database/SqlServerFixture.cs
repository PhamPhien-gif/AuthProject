using AuthProject.TestSupport;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace AuthProject.IntegrationTests.Database;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? container;
    private string[] allowedServers = [];
    private bool created;
    public string ConnectionString { get; private set; } = string.Empty;
    public string DatabaseName { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            var template = Environment.GetEnvironmentVariable("ConnectionStrings__TestConnection");
            if (string.IsNullOrWhiteSpace(template))
            {
                // No fallback to DefaultConnection. CI uses an isolated container by default.
                container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU22-GDR1-ubuntu-22.04@sha256:bf438d7104f861f5e1e1ba14b063d60a5bd883964b3c9b3b3c0064536177baa5").Build();
                await container.StartAsync();
                var builder = new SqlConnectionStringBuilder(container.GetConnectionString())
                {
                    InitialCatalog = "AuthProject_Test_Template"
                };
                template = builder.ConnectionString;
                allowedServers = [builder.DataSource];
            }
            else
            {
                allowedServers = (Environment.GetEnvironmentVariable("AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS") ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            ConnectionString = DatabaseTarget.CreateIsolatedConnectionString(template, allowedServers);
            DatabaseName = DatabaseTarget.GetDatabaseName(ConnectionString, allowedServers);
            await ExecuteMasterAsync($"CREATE DATABASE [{DatabaseName}]");
            created = true;
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (created)
            {
                // Revalidate the exact target this fixture created before any destructive SQL.
                var verifiedName = DatabaseTarget.GetDatabaseName(ConnectionString, allowedServers);
                if (!string.Equals(verifiedName, DatabaseName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Test database target changed during fixture lifetime.");
                await ExecuteMasterAsync($"ALTER DATABASE [{verifiedName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{verifiedName}]");
                created = false;
            }
        }
        finally
        {
            if (container is not null)
            {
                await container.DisposeAsync();
                container = null;
            }
        }
    }

    private async Task ExecuteMasterAsync(string sql)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync();
    }
}
