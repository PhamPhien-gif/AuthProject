using System.Text;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace AuthProject.Configuration;

public static class LocalDotEnv
{
    // Deliberately a local, single-line dotenv format. No shell execution or variable expansion.
    public static IReadOnlyDictionary<string, string?> Parse(string text)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text);
        var lineNumber = 0;
        while (reader.ReadLine() is { } rawLine)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals <= 0) throw InvalidLine(lineNumber);
            var key = line[..equals].Trim();
            if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_')
                || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw InvalidLine(lineNumber);
            values[key] = ParseValue(line[(equals + 1)..].Trim(), lineNumber);
        }
        return values;
    }

    public static string? FindFile(string directory)
    {
        var start = new DirectoryInfo(Path.GetFullPath(directory));
        for (var current = start; current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "AuthProject.sln")))
                return Existing(Path.Combine(current.FullName, "AuthProject", ".env"), Path.Combine(current.FullName, ".env"));
            if (File.Exists(Path.Combine(current.FullName, "AuthProject.csproj")))
            {
                var solution = current.Parent;
                return solution is not null && File.Exists(Path.Combine(solution.FullName, "AuthProject.sln"))
                    ? Existing(Path.Combine(current.FullName, ".env"), Path.Combine(solution.FullName, ".env"))
                    : Existing(Path.Combine(current.FullName, ".env"));
            }
            // Never pick up a different repository's parent .env.
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return null;
        }
        return Existing(Path.Combine(start.FullName, ".env"));
    }

    public static void AddToConfiguration(IConfigurationBuilder configuration, string environmentName, string directory)
    {
        if (!string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase)) return;
        var file = FindFile(directory);
        if (file is null) return;
        var values = Parse(File.ReadAllText(file)).Select(entry =>
            new KeyValuePair<string, string?>(entry.Key.Replace("__", ":", StringComparison.Ordinal), entry.Value));
        var sources = configuration.Sources;
        var index = -1;
        // The final environment provider is the application's provider after appsettings/user secrets.
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is EnvironmentVariablesConfigurationSource) index = i;
        if (index < 0)
        {
            index = sources.Count;
            for (var i = 0; i < sources.Count; i++)
                if (sources[i] is CommandLineConfigurationSource) { index = i; break; }
        }
        sources.Insert(index, new MemoryConfigurationSource { InitialData = values });
    }

    private static string? Existing(params string[] files) => files.FirstOrDefault(File.Exists);

    private static string ParseValue(string value, int lineNumber)
    {
        if (value.Length == 0 || value.StartsWith('#')) return string.Empty;
        if (value[0] is not '\'' and not '"')
        {
            for (var i = 1; i < value.Length; i++)
                if (value[i] == '#' && char.IsWhiteSpace(value[i - 1])) return value[..i].TrimEnd();
            return value;
        }

        var quote = value[0];
        var result = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            // Preserve SQL/Windows backslashes, including \n. Only a double quote can be escaped.
            if (quote == '"' && value[i] == '\\' && i + 1 < value.Length && value[i + 1] == '"')
            {
                result.Append('"');
                i++;
                continue;
            }
            if (value[i] == quote)
            {
                var tail = value[(i + 1)..].TrimStart();
                if (tail.Length != 0 && !tail.StartsWith('#')) throw InvalidLine(lineNumber);
                return result.ToString();
            }
            result.Append(value[i]);
        }
        throw InvalidLine(lineNumber);
    }

    private static FormatException InvalidLine(int lineNumber) =>
        new($"Invalid .env syntax at line {lineNumber}. Use KEY=value with optional single/double quotes.");
}
