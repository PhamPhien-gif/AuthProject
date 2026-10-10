using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using AuthProject.Exceptions;

namespace AuthProject.Security;

public enum GoogleFlow { Login, Link, Reauthenticate }
public sealed record GoogleBinding(Guid ContextId, GoogleFlow Flow, DateTimeOffset StartedAt,
    PasswordActor? Actor = null, Guid? ChallengeId = null, string? Subject = null, Guid? UserId = null);
public sealed class GoogleBrowserContext(IDataProtectionProvider provider)
{
    public const string FlowCookie = "__Host-AuthProject.GoogleFlow", CompletionCookie = "__Secure-AuthProject.GoogleCompletion";
    private readonly IDataProtector flow = provider.CreateProtector("AuthProject.GoogleFlow.v1");
    private readonly IDataProtector completion = provider.CreateProtector("AuthProject.GoogleCompletion.v1");
    public void Start(HttpResponse response, GoogleBinding binding) => response.Cookies.Append(FlowCookie,
        flow.Protect(JsonSerializer.Serialize(binding)), Flags(false));
    public void Complete(HttpResponse response, GoogleBinding binding)
    {
        response.Cookies.Delete(FlowCookie, Flags(false));
        response.Cookies.Append(CompletionCookie, completion.Protect(JsonSerializer.Serialize(binding)), Flags(true));
    }
    public GoogleBinding Read(HttpRequest request, bool completed)
    {
        var value = request.Cookies[completed ? CompletionCookie : FlowCookie];
        if (value is null || value.Length > 4096) throw new AppException("AUTH_GOOGLE_INVALID");
        try { return JsonSerializer.Deserialize<GoogleBinding>((completed ? completion : flow).Unprotect(value))
            ?? throw new AppException("AUTH_GOOGLE_INVALID"); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { throw new AppException("AUTH_GOOGLE_INVALID"); }
    }
    public void Clear(HttpResponse response)
    {
        response.Cookies.Delete(FlowCookie, Flags(false)); response.Cookies.Delete(CompletionCookie, Flags(true));
    }
    private static CookieOptions Flags(bool completed) => new() { HttpOnly = true, Secure = true, IsEssential = true,
        SameSite = completed ? SameSiteMode.Strict : SameSiteMode.Lax, Path = completed ? "/api/v1/auth" : "/" };
}
