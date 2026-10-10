using AuthProject.Entities;
using AuthProject.Logging;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;

namespace UnitTestAuth.Logging;

[Trait("Category", "Unit")]
public sealed class AuditDeliveryTests
{
    private sealed class BrokenAppender : AppenderSkeleton
    { protected override void Append(LoggingEvent value) => ErrorHandler.Error("disk unavailable"); }
    [Fact]
    public void Sink_does_not_report_success_when_log4net_swallows_file_error()
    {
        var repo = LogManager.CreateRepository("broken-" + Guid.NewGuid());
        try
        {
            BasicConfigurator.Configure(repo, new BrokenAppender());
            var sink = new Log4NetAuditSink(LogManager.GetLogger(repo.Name, "Auth.Audit"));
            Assert.Throws<IOException>(() => sink.Write(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new(AuditEventType.LoginCompleted, AuditOutcome.Success, AuditReason.None, "trace"))));
        }
        finally { repo.Shutdown(); }
    }
}
