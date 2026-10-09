using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AuthProject.TestSupport;

internal static class DatabaseTarget
{
    public static string CreateIsolatedConnectionString(string template, IReadOnlyCollection<string> allowedServers)
    {
        _ = GetDatabaseName(template, allowedServers);
        var builder = new SqlConnectionStringBuilder(template)
        {
            InitialCatalog = $"AuthProject_Test_{Guid.NewGuid():N}",
            Pooling = false,
            ConnectTimeout = 15
        };
        return builder.ConnectionString;
    }

    public static string GetDatabaseName(string connectionString, IReadOnlyCollection<string> allowedServers)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!allowedServers.Contains(builder.DataSource, StringComparer.OrdinalIgnoreCase)
            || !Regex.IsMatch(builder.InitialCatalog, @"\AAuthProject_Test_[A-Za-z0-9_]{1,64}\z",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            || !string.IsNullOrEmpty(builder.AttachDBFilename)
            || !string.IsNullOrEmpty(builder.FailoverPartner)
            || builder.UserInstance)
        {
            throw new InvalidOperationException(
                "Test SQL requires an explicitly allowed server and an AuthProject_Test_ database; alternate targets are forbidden.");
        }

        return builder.InitialCatalog;
    }
}
