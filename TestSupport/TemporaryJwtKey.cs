using System.Security.Cryptography;
using AuthProject.Configuration;

namespace AuthProject.TestSupport;

public sealed class TemporaryJwtKey : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AuthProject-Test-" + Guid.NewGuid() + ".pem");
    public RSA Rsa { get; } = RSA.Create(2048);
    public TemporaryJwtKey() => File.WriteAllText(Path, Rsa.ExportPkcs8PrivateKeyPem());
    public JwtOptions Settings => new() { Enabled = true, Issuer = "https://issuer.example.test", Audience = "test-api", SigningKeyPath = Path, KeyId = "test-key", ClockSkewSeconds = 0 };
    public void Dispose() { Rsa.Dispose(); File.Delete(Path); }
}
