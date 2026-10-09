namespace AuthProject.Security;

public static class EmailNormalizer
{
    public static string Normalize(string email) => email.Trim().ToUpperInvariant();
}
