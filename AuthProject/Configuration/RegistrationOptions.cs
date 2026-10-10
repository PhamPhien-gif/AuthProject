namespace AuthProject.Configuration;

public sealed class RegistrationOptions
{
    public bool Enabled { get; set; }
    public string VerificationUrl { get; set; } = string.Empty;
    public int TokenLifetimeSeconds { get; set; } = 3600;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int SendWindowSeconds { get; set; } = 900;
    public int MaxSendsPerWindow { get; set; } = 3;
    public int PollSeconds { get; set; } = 5;
    public int SmtpTimeoutSeconds { get; set; } = 30;
    public int LeaseSeconds { get; set; } = 120;
}
