using System.Security.Cryptography;

namespace AuthProject.Security.Authentication;

public static class JwtKeyFile
{
    public static void Generate(string path)
    {
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var rsa = RSA.Create(3072);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(target, fileOptions);
        using var writer = new StreamWriter(stream);
        writer.Write(rsa.ExportPkcs8PrivateKeyPem());
    }
}
