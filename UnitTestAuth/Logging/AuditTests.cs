using System.Security.Cryptography;
using AuthProject.Entities;
using AuthProject.Logging;
using log4net;
using log4net.Appender;
using log4net.Config;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace UnitTestAuth.Logging;

[Trait("Category", "Unit")]
public sealed class AuditTests
{
    private static AuditEnvelope CreateEnvelope() => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        new(AuditEventType.LoginCompleted, AuditOutcome.Success, AuditReason.None, "trace_123",
            Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), AuthenticationMethod.Password, 1)));

    [Fact]
    public void ProtectedPayload_RoundTripsAndRejectsTamperingOrDifferentKey()
    {
        var protector = new AuditPayloadProtector(new EphemeralDataProtectionProvider());
        var envelope = CreateEnvelope();
        var protectedPayload = protector.Protect(envelope);
        Assert.DoesNotContain(envelope.Record.TraceId, protectedPayload);
        Assert.Equal(envelope, protector.Unprotect(protectedPayload));
        var bytes = WebEncoders.Base64UrlDecode(protectedPayload);
        bytes[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => protector.Unprotect(WebEncoders.Base64UrlEncode(bytes)));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("invalid"));
        Assert.Throws<CryptographicException>(() =>
            new AuditPayloadProtector(new EphemeralDataProtectionProvider()).Unprotect(protectedPayload));
    }

    [Fact]
    public void LogSink_EmitsStructuredAllowlistedEventWithoutCredentialsOrMultilineInjection()
    {
        var repository = LogManager.CreateRepository("audit-test-" + Guid.NewGuid());
        try
        {
            var appender = new MemoryAppender();
            BasicConfigurator.Configure(repository, appender);
            var sink = new Log4NetAuditSink(LogManager.GetLogger(repository.Name, "Auth.Audit"));
            var envelope = CreateEnvelope();
            sink.Write(envelope);
            var text = Assert.Single(appender.GetEvents()).RenderedMessage;
            Assert.NotNull(text);
            using var json = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal(envelope.EventId, json.RootElement.GetProperty("eventId").GetGuid());
            Assert.Contains("LoginCompleted", text);
            Assert.DoesNotContain("\n", text);
            foreach (var forbidden in new[] { "passwordHash", "refreshToken", "accessToken", "otp", "email", "authorization", "cookie", "protectedPayload" })
                Assert.DoesNotContain("\"" + forbidden + "\"", text, StringComparison.OrdinalIgnoreCase);
        }
        finally { repository.Shutdown(); }
    }

    [Theory]
    [InlineData("trace\r\nFORGED-AUDIT")]
    [InlineData("")]
    public void LogSink_WhenTraceIdentifierInvalid_RejectsWithoutLogging(string traceId)
    {
        var repository = LogManager.CreateRepository("audit-invalid-test-" + Guid.NewGuid());
        try
        {
            var appender = new MemoryAppender();
            BasicConfigurator.Configure(repository, appender);
            var sink = new Log4NetAuditSink(LogManager.GetLogger(repository.Name, "Auth.Audit"));
            var error = Assert.Throws<ArgumentException>(() => sink.Write(CreateEnvelope() with
            {
                Record = CreateEnvelope().Record with { TraceId = traceId }
            }));
            Assert.Empty(appender.GetEvents());
            Assert.DoesNotContain("FORGED-AUDIT", error.Message);
        }
        finally { repository.Shutdown(); }
    }
}
