using System.Security.Cryptography;
using AuthProject.Security.Authentication;

namespace UnitTestAuth.Security;

[Trait("Category", "Unit")]
public sealed class JwtKeyFileTests
{
    [Fact]
    public void Generate_CreatesUsablePrivateKeyAndDoesNotOverwriteExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "AuthProject-Test-" + Guid.NewGuid() + ".pem");
        try
        {
            JwtKeyFile.Generate(path);
            var original = File.ReadAllText(path);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(original);
            Assert.Equal(3072, rsa.KeySize);
            Assert.NotNull(rsa.ExportParameters(true).D);
            Assert.Throws<IOException>(() => JwtKeyFile.Generate(path));
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
