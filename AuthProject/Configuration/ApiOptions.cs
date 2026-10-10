namespace AuthProject.Configuration;

public sealed class ApiOptions
{
    public const string Section = "Api";
    public int MaxRequestBodyBytes { get; set; } = 16_384;
    public int RequestTimeoutSeconds { get; set; } = 15;
}

public sealed class JwtOptions
{
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SigningKeyPath { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public int AccessTokenLifetimeSeconds { get; set; } = 300;
    public int ClockSkewSeconds { get; set; } = 30;
    public int SessionIdleLifetimeSeconds { get; set; } = 604800;
    public int SessionAbsoluteLifetimeSeconds { get; set; } = 2592000;
}

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string Sender { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class GoogleOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = "/signin-google";
}

public sealed class EmailOtpOptions
{
    public bool Enabled { get; set; }
    public string Pepper { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public int ExpirySeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
}
