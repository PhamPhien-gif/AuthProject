using AuthProject.Configuration;
using Microsoft.Extensions.Configuration;

namespace UnitTestAuth.Configuration;

[Trait("Category", "Unit")]
public sealed class LocalDotEnvTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "AuthProject_DotEnv_" + Guid.NewGuid().ToString("N"));

    public LocalDotEnvTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "AuthProject.sln"), "test marker");
        Directory.CreateDirectory(ProjectDirectory);
        File.WriteAllText(Path.Combine(ProjectDirectory, "AuthProject.csproj"), "test marker");
    }

    private string ProjectDirectory => Path.Combine(root, "AuthProject");

    [Fact]
    public void Parse_WhenSqlConnectionQuotedOrUnquoted_PreservesBackslashEqualsAndSemicolons()
    {
        const string connection = "Server=HOST\\SQL;Database=AuthProject;Password=a=b;Encrypt=True;";
        var parsed = LocalDotEnv.Parse("# comment\r\n\r\nConnectionStrings__DefaultConnection='" + connection
            + "' # local\r\nexport COPY=\"" + connection + "\"\r\nRAW=" + connection + "\r\nEMPTY=\r\nEMPTY_COMMENT= # comment\r\nHASH=abc#123\r\nINLINE=abc # comment\r\n");
        Assert.Equal(connection, parsed["ConnectionStrings__DefaultConnection"]);
        Assert.Equal(connection, parsed["COPY"]);
        Assert.Equal(connection, parsed["RAW"]);
        Assert.Equal(string.Empty, parsed["EMPTY"]);
        Assert.Equal(string.Empty, parsed["EMPTY_COMMENT"]);
        Assert.Equal("abc#123", parsed["HASH"]);
        Assert.Equal("abc", parsed["INLINE"]);
    }

    [Fact]
    public void Parse_WhenValuesContainShellSyntaxOrQuotedHash_TreatsThemAsLiteralText()
    {
        var parsed = LocalDotEnv.Parse("VALUE='$(DoNotExecute) ${DO_NOT_EXPAND} # literal'\nQUOTED=\"a\\\"b\\new\"\nDUP=first\nDUP=last\n");
        Assert.Equal("$(DoNotExecute) ${DO_NOT_EXPAND} # literal", parsed["VALUE"]);
        Assert.Equal("a\"b\\new", parsed["QUOTED"]);
        Assert.Equal("last", parsed["DUP"]);
    }

    [Theory]
    [InlineData("PASSWORD=\"DO_NOT_LEAK")]
    [InlineData("PASSWORD='DO_NOT_LEAK' trailing")]
    [InlineData("INVALID KEY=DO_NOT_LEAK")]
    [InlineData("=DO_NOT_LEAK")]
    [InlineData("DO_NOT_LEAK")]
    public void Parse_WhenMalformed_ReportsLineNumberWithoutValue(string line)
    {
        var error = Assert.Throws<FormatException>(() => LocalDotEnv.Parse("# comment\n" + line));
        Assert.Contains("2", error.Message);
        Assert.DoesNotContain("DO_NOT_LEAK", error.Message);
    }

    [Fact]
    public void FindFile_FromSolutionOrProject_PrefersProjectThenSolutionAndStopsAtBoundary()
    {
        var solutionFile = Path.Combine(root, ".env");
        var projectFile = Path.Combine(ProjectDirectory, ".env");
        File.WriteAllText(solutionFile, "VALUE=solution");
        Assert.Equal(solutionFile, LocalDotEnv.FindFile(root));
        Assert.Equal(solutionFile, LocalDotEnv.FindFile(ProjectDirectory));
        File.WriteAllText(projectFile, "VALUE=project");
        Assert.Equal(projectFile, LocalDotEnv.FindFile(root));
        Assert.Equal(projectFile, LocalDotEnv.FindFile(ProjectDirectory));
        var unrelated = Path.Combine(root, "OtherRepository");
        Directory.CreateDirectory(Path.Combine(unrelated, ".git"));
        Assert.Null(LocalDotEnv.FindFile(unrelated));
    }

    [Fact]
    public void Configuration_WhenDotEnvLoaded_OverridesDefaultsButPreservesEnvironmentAndCommandLine()
    {
        File.WriteAllText(Path.Combine(root, ".env"), "LOCAL_ONLY=dotenv\nOVERRIDE=dotenv\nCLI=dotenv\nConnectionStrings__DefaultConnection='Server=HOST\\SQL;Database=TestOnly;'\n");
        var prefix = "AuthProject_DotEnv_" + Guid.NewGuid().ToString("N") + "_";
        var originalValue = Environment.GetEnvironmentVariable("LOCAL_ONLY");
        Environment.SetEnvironmentVariable(prefix + "OVERRIDE", "environment");
        try
        {
            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LOCAL_ONLY"] = "default" })
                .AddEnvironmentVariables(prefix)
                .AddCommandLine(["--CLI=command-line"]);
            LocalDotEnv.AddToConfiguration(builder, "Development", root);
            using var config = (ConfigurationRoot)builder.Build();
            Assert.Equal("dotenv", config["LOCAL_ONLY"]);
            Assert.Equal("environment", config["OVERRIDE"]);
            Assert.Equal("command-line", config["CLI"]);
            Assert.Equal("Server=HOST\\SQL;Database=TestOnly;", config.GetConnectionString("DefaultConnection"));
            Assert.Equal(originalValue, Environment.GetEnvironmentVariable("LOCAL_ONLY"));
        }
        finally { Environment.SetEnvironmentVariable(prefix + "OVERRIDE", null); }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void Configuration_OutsideDevelopment_DoesNotReadEvenMalformedDotEnv(string environment)
    {
        File.WriteAllText(Path.Combine(root, ".env"), "PASSWORD='DO_NOT_LEAK");
        var builder = new ConfigurationBuilder();
        LocalDotEnv.AddToConfiguration(builder, environment, root);
        using var config = (ConfigurationRoot)builder.Build();
        Assert.Null(config["PASSWORD"]);
    }

    [Fact]
    public void Configuration_WhenDotEnvMissing_KeepsExistingConfiguration()
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VALUE"] = "default" });
        LocalDotEnv.AddToConfiguration(builder, "Development", root);
        using var config = (ConfigurationRoot)builder.Build();
        Assert.Equal("default", config["VALUE"]);
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("AuthProject_DotEnv_", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test temporary directory.");
        Directory.Delete(fullPath, recursive: true);
    }
}
