using AuthProject.TestSupport;
using Microsoft.Data.SqlClient;

namespace UnitTestAuth.Harness;

[Trait("Category", "Unit")]
public sealed class DatabaseTargetTests
{
    private static readonly string[] AllowedServers = ["localhost", @"LAPTOP-LTSU53RG\SQLEXPRESS01"];

    [Theory]
    [InlineData("AuthProject")]
    [InlineData("master")]
    [InlineData("")]
    [InlineData("AuthProject_Test_")]
    [InlineData("AuthProject_Test_x]; DROP DATABASE AuthProject;--")]
    public void CreateIsolatedConnectionString_WhenDatabaseIsNotSafe_RejectsBeforeConnecting(string database)
    {
        var template = new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = database };
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseTarget.CreateIsolatedConnectionString(template.ConnectionString, AllowedServers));
    }

    [Fact]
    public void CreateIsolatedConnectionString_WhenServerIsNotExplicitlyAllowed_RejectsBeforeConnecting()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseTarget.CreateIsolatedConnectionString(
            "Server=production.example;Database=AuthProject_Test_Local", AllowedServers));
    }

    [Theory]
    [InlineData("AttachDbFilename=C:\\data\\AuthProject.mdf")]
    [InlineData("Failover Partner=production.example")]
    [InlineData("User Instance=True")]
    public void CreateIsolatedConnectionString_WhenConnectionCanRedirectTarget_Rejects(string extra)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseTarget.CreateIsolatedConnectionString(
            "Server=localhost;Database=AuthProject_Test_Local;" + extra, AllowedServers));
    }

    [Fact]
    public void CreateIsolatedConnectionString_WhenTemplateIsSafe_GeneratesFreshDatabaseAndPreservesAuthentication()
    {
        const string template = "Server=localhost;Database=AuthProject_Test_Local;Integrated Security=True;Encrypt=True;TrustServerCertificate=True";
        var first = new SqlConnectionStringBuilder(DatabaseTarget.CreateIsolatedConnectionString(template, AllowedServers));
        var second = new SqlConnectionStringBuilder(DatabaseTarget.CreateIsolatedConnectionString(template, AllowedServers));

        Assert.Matches("^AuthProject_Test_[a-f0-9]{32}$", first.InitialCatalog);
        Assert.NotEqual(first.InitialCatalog, second.InitialCatalog);
        Assert.NotEqual("AuthProject_Test_Local", first.InitialCatalog);
        Assert.Equal("localhost", first.DataSource);
        Assert.True(first.IntegratedSecurity);
        Assert.False(first.Pooling);
        Assert.Equal(first.InitialCatalog, DatabaseTarget.GetDatabaseName(first.ConnectionString, AllowedServers));
    }
}
