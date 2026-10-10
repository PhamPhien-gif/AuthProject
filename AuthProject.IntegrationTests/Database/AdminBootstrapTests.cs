using System.Diagnostics;
using AuthProject.Data;
using AuthProject.Data.Configurations;
using AuthProject.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthProject.IntegrationTests.Database;

[Trait("Category", "Integration"), Trait("Dependency", "SqlServer")]
public sealed class AdminBootstrapTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string Password = "a long bootstrap secret 123!";
    private AuthDbContext Db() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private async Task<(int Code, string Output)> Command(string email, string password = Password)
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AuthProject", "AuthProject.csproj"));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "run", "--project", project, "--configuration", configuration, "--no-build", "--no-launch-profile", "--", "--bootstrap-admin" }) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing"; start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["Persistence__Enabled"] = "true"; start.Environment["ConnectionStrings__DefaultConnection"] = fixture.ConnectionString;
        start.Environment["Persistence__DataProtectionKeyDirectory"] = Path.Combine(Path.GetTempPath(), "AuthProject_Test_Bootstrap_" + fixture.DatabaseName);
        foreach (var flag in new[] { "Jwt", "Email", "Refresh", "Registration", "PasswordRecovery", "Google", "EmailOtp" }) start.Environment[$"Auth__{flag}__Enabled"] = "false";
        start.Environment["Audit__Log4Net__Enabled"] = "false";
        start.Environment["Audit__Delivery__Enabled"] = "false"; start.Environment["Audit__Retention__Enabled"] = "false";
        start.Environment["AUTHPROJECT_BOOTSTRAP_ADMIN_EMAIL"] = email; start.Environment["AUTHPROJECT_BOOTSTRAP_ADMIN_PASSWORD"] = password;
        start.Environment["AUTHPROJECT_BOOTSTRAP_ADMIN_DISPLAY_NAME"] = "Bootstrap Admin";
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var text = await output + await errors; Assert.DoesNotContain(password, text); return (process.ExitCode, text);
    }
    [Fact]
    public async Task Actual_command_creates_hashed_admin_and_replay_preserves_credentials_and_MFA()
    {
        await using var db = Db(); await db.Database.MigrateAsync(); var email = Guid.NewGuid() + "@example.test";
        var created = await Command(email); Assert.Equal(0, created.Code); Assert.Contains("Admin created", created.Output);
        var user = await db.Users.SingleAsync(u => u.Email == email); Assert.NotNull(user.EmailConfirmedAt); Assert.False(user.MfaEmailEnabled);
        Assert.Equal(UserStatus.Active, user.Status); Assert.NotEqual(Password, user.PasswordHash);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash!, Password));
        Assert.Equal(2, await db.UserRoles.CountAsync(r => r.UserId == user.Id)); var hash = user.PasswordHash;
        user.MfaEmailEnabled = true; await db.SaveChangesAsync();
        var replay = await Command(email, "a different bootstrap password!"); Assert.Equal(0, replay.Code); Assert.Contains("already exists", replay.Output);
        var unchanged = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id); Assert.Equal(hash, unchanged.PasswordHash); Assert.True(unchanged.MfaEmailEnabled);
        Assert.Single(await db.AuditEvents.Where(a => a.SubjectId == user.Id && a.EventType == AuditEventType.AdminBootstrapped).ToArrayAsync());
        Assert.False(await db.Sessions.AnyAsync(s => s.UserId == user.Id));
    }
    [Fact]
    public async Task Bootstrap_refuses_existing_non_admin_and_invalid_input_without_leaking_secret()
    {
        await using var db = Db(); await db.Database.MigrateAsync(); var user = new User { Email = Guid.NewGuid() + "@example.test", DisplayName = "Existing", Status = UserStatus.Active, EmailConfirmedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user); db.UserRoles.Add(new() { UserId = user.Id, RoleId = SystemRoles.UserId }); await db.SaveChangesAsync();
        var collision = await Command(user.Email); Assert.Equal(1, collision.Code); Assert.Contains("RESOURCE_CONFLICT", collision.Output);
        var invalid = await Command("invalid-email", "short-secret"); Assert.Equal(1, invalid.Code); Assert.Contains("VALIDATION_FAILED", invalid.Output);
        Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == SystemRoles.AdminId));
    }
    [Fact]
    public async Task Concurrent_bootstrap_is_idempotent_and_writes_one_audit()
    {
        await using var db = Db(); await db.Database.MigrateAsync(); var email = Guid.NewGuid() + "@example.test";
        var results = await Task.WhenAll(Command(email), Command(email)); Assert.All(results, r => Assert.Equal(0, r.Code));
        var user = await db.Users.SingleAsync(u => u.Email == email);
        Assert.Single(await db.AuditEvents.Where(a => a.SubjectId == user.Id && a.EventType == AuditEventType.AdminBootstrapped).ToArrayAsync());
    }
}
