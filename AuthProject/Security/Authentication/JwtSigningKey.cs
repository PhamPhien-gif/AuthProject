using System.Security.Cryptography;
using AuthProject.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthProject.Security.Authentication;

public sealed class JwtSigningKey : IDisposable
{
    private readonly RSA privateKey = RSA.Create();
    private readonly RSA publicKey = RSA.Create();
    public SigningCredentials Credentials { get; }
    public SecurityKey ValidationKey { get; }
    public JwtSigningKey(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        try
        {
            var settings = options.Value;
            if (!settings.Enabled) throw new InvalidOperationException();
            var path = Path.GetFullPath(settings.SigningKeyPath, environment.ContentRootPath);
            if (new FileInfo(path).Length > 16384) throw new InvalidOperationException();
            privateKey.ImportFromPem(File.ReadAllText(path));
            if (privateKey.KeySize < 2048 || privateKey.KeySize > 8192) throw new InvalidOperationException();
            _ = privateKey.ExportParameters(true); // A public-only key cannot sign access tokens.
            publicKey.ImportParameters(privateKey.ExportParameters(false));
            Credentials = new SigningCredentials(new RsaSecurityKey(privateKey) { KeyId = settings.KeyId }, SecurityAlgorithms.RsaSha256);
            ValidationKey = new RsaSecurityKey(publicKey) { KeyId = settings.KeyId };
        }
        catch
        {
            privateKey.Dispose(); publicKey.Dispose();
            throw new OptionsValidationException(nameof(JwtOptions), typeof(JwtOptions), ["Enabled JWT requires a readable RSA private PEM key of at least 2048 bits."]);
        }
    }
    public void Dispose() { privateKey.Dispose(); publicKey.Dispose(); }
}
