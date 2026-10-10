using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AuthProject.Services.Email;

public sealed class EmailPayloadProtector(IDataProtectionProvider provider) : IEmailPayloadProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("AuthProject.VerificationEmail.v1");
    public string Protect(VerificationEmail email) => protector.Protect(JsonSerializer.Serialize(email));
    public VerificationEmail Unprotect(string payload) => JsonSerializer.Deserialize<VerificationEmail>(protector.Unprotect(payload))
        ?? throw new InvalidOperationException("Invalid email payload.");
}
