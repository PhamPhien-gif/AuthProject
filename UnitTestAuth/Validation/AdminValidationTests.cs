using AuthProject.DTOs;
using AuthProject.DTOs.Validators;

namespace UnitTestAuth.Validation;

[Trait("Category", "Unit")]
public sealed class AdminValidationTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("too-long")]
    public async Task Profile_rejects_invalid_display_names(string? name)
    { Assert.False((await new UpdateProfileRequestValidator().ValidateAsync(new UpdateProfileRequest(name == "too-long" ? new string('x', 101) : name))).IsValid); }
    [Fact]
    public async Task Query_rejects_invalid_paging_filters_and_search()
    {
        var result = await new AdminUserQueryValidator().ValidateAsync(new AdminUserQuery { Page = 0, PageSize = 101, Status = "Admin", Search = new string('x', 101) });
        Assert.Equal(4, result.Errors.Count);
        Assert.True((await new AdminUserQueryValidator().ValidateAsync(new AdminUserQuery { Status = "PendingVerification", Search = "user" })).IsValid);
    }
    [Fact]
    public async Task Target_requires_non_empty_id()
    { Assert.False((await new UserTargetRequestValidator().ValidateAsync(new UserTargetRequest())).IsValid); }
}
