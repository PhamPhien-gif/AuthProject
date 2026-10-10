using AuthProject.DTOs;
using AuthProject.DTOs.Validators;

namespace UnitTestAuth.Validation;

[Trait("Category", "Unit")]
public sealed class AuditQueryTests
{
    [Theory]
    [InlineData("numeric")] [InlineData("unknown")] [InlineData("trace")] [InlineData("range")] [InlineData("paging")]
    public async Task Audit_filters_reject_unbounded_or_non_allowlisted_input(string state)
    {
        var query = new AuditQuery { EventType = state == "numeric" ? "1" : state == "unknown" ? "DeleteAll" : null,
            TraceId = state == "trace" ? "secret\r\n" : null, From = state == "range" ? DateTimeOffset.UtcNow : null,
            PageSize = state == "paging" ? 101 : 20 };
        Assert.False((await new AuditQueryValidator().ValidateAsync(query)).IsValid);
    }
}
