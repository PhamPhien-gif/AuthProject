using AuthProject.DTOs;
using AuthProject.DTOs.Validators;
using FluentValidation.TestHelper;

namespace UnitTestAuth.Validation;

[Trait("Category", "Unit")]
public sealed class RequestValidationTests
{
    [Fact]
    public async Task Register_WhenFieldsInvalid_ReturnsAllFieldErrors()
    {
        var result = await new RegisterRequestValidator().TestValidateAsync(new RegisterRequest("invalid", "short", " "));
        result.ShouldHaveValidationErrorFor(x => x.Email).WithErrorCode("EMAIL_INVALID");
        result.ShouldHaveValidationErrorFor(x => x.Password).WithErrorCode("PASSWORD_LENGTH");
        result.ShouldHaveValidationErrorFor(x => x.DisplayName).WithErrorCode("DISPLAY_NAME_REQUIRED");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    public async Task Email_WhenInvalid_IsRejected(string? email)
    {
        var result = await new EmailRequestValidator().TestValidateAsync(new EmailRequest(email));
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Theory]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Register_WhenPasswordAtLengthBoundary_AppliesPolicyWithoutTrimming(int length, bool valid)
    {
        var request = new RegisterRequest(" user@example.com ", new string(' ', length - 1) + "a", " Name ");
        var result = await new RegisterRequestValidator().ValidateAsync(request);
        Assert.Equal(valid, result.IsValid);
        Assert.Equal(length, request.Password!.Length);
    }

    [Fact]
    public async Task Register_WhenEmailOrDisplayNameTooLong_Rejects()
    {
        var result = await new RegisterRequestValidator().TestValidateAsync(
            new RegisterRequest(new string('a', 250) + "@test.com", new string('a', 15), new string('n', 101)));
        result.ShouldHaveValidationErrorFor(x => x.Email);
        result.ShouldHaveValidationErrorFor(x => x.DisplayName);
    }

    [Fact]
    public async Task Login_WhenLegacyPasswordIsShort_DoesNotApplyNewPasswordMinimum()
    {
        var result = await new LoginRequestValidator().ValidateAsync(new LoginRequest("u@example.com", "old"));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("012345", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("１２３４５６", false)]
    [InlineData("123456\n", false)]
    [InlineData(null, false)]
    public async Task Otp_WhenFormatVaries_RequiresExactlySixAsciiDigits(string? code, bool valid)
    {
        var result = await new VerifyOtpRequestValidator().ValidateAsync(new VerifyOtpRequest(Guid.NewGuid(), code));
        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public async Task Challenges_WhenEmptyIdOrUnsafeToken_Rejects()
    {
        Assert.False((await new ChallengeRequestValidator().ValidateAsync(new ChallengeRequest(Guid.Empty))).IsValid);
        Assert.False((await new VerifyEmailRequestValidator().ValidateAsync(new VerifyEmailRequest("bad+token="))).IsValid);
        Assert.False((await new ResetPasswordRequestValidator().ValidateAsync(new ResetPasswordRequest("ok_token", "short"))).IsValid);
        Assert.False((await new GoogleCompleteRequestValidator().ValidateAsync(new GoogleCompleteRequest(new string('a', 513)))).IsValid);
        Assert.False((await new RecoveryCodeRequestValidator().ValidateAsync(new RecoveryCodeRequest(Guid.Empty, ""))).IsValid);
    }

    [Fact]
    public async Task ChangePassword_WhenUnchanged_Rejects()
    {
        var password = new string('a', 15);
        Assert.False((await new ChangePasswordRequestValidator().ValidateAsync(new ChangePasswordRequest(password, password))).IsValid);
    }

    [Fact]
    public async Task PagingAndHistory_WhenOutOfBounds_Rejects()
    {
        Assert.False((await new PagingRequestValidator().ValidateAsync(new PagingRequest { Page = 0, PageSize = 101 })).IsValid);
        Assert.False((await new HistoryQueryValidator().ValidateAsync(new HistoryQuery
        {
            From = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            To = DateTimeOffset.Parse("2026-05-01T00:00:00Z"), Sort = "sql-injection"
        })).IsValid);
        Assert.True((await new HistoryQueryValidator().ValidateAsync(new HistoryQuery())).IsValid);
    }

    [Fact]
    public async Task Admin_WhenDuplicateRolesOrUnsupportedStatus_Rejects()
    {
        Assert.False((await new SetUserRolesRequestValidator().ValidateAsync(new SetUserRolesRequest(["Admin", "admin"]))).IsValid);
        Assert.False((await new SetUserRolesRequestValidator().ValidateAsync(new SetUserRolesRequest(["Owner"]))).IsValid);
        Assert.False((await new ChangeUserStatusRequestValidator().ValidateAsync(new ChangeUserStatusRequest("Verified"))).IsValid);
    }
}
