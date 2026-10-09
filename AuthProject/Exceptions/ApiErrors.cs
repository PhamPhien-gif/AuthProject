namespace AuthProject.Exceptions;

public sealed record ApiError(string Code, int Status, string Title)
{
    public string Type => "urn:authproject:problem:" + Code.ToLowerInvariant().Replace('_', '-');
}

public static class ApiErrors
{
    public static readonly ApiError InvalidRequest = new("INVALID_REQUEST", 400, "Yêu cầu không hợp lệ.");
    public static readonly ApiError ValidationFailed = new("VALIDATION_FAILED", 400, "Dữ liệu không hợp lệ.");
    public static readonly ApiError Internal = new("INTERNAL_ERROR", 500, "Đã xảy ra lỗi hệ thống.");

    private static readonly ApiError[] Definitions =
    [
        InvalidRequest, ValidationFailed, Internal,
        new("AUTH_CHALLENGE_INVALID", 400, "Challenge không hợp lệ."),
        new("AUTH_INVALID_CREDENTIALS", 401, "Thông tin đăng nhập không hợp lệ."),
        new("AUTH_UNAUTHENTICATED", 401, "Yêu cầu xác thực."),
        new("AUTH_SESSION_INVALID", 401, "Phiên không hợp lệ."),
        new("AUTH_REFRESH_INVALID", 401, "Refresh token không hợp lệ."),
        new("AUTH_FORBIDDEN", 403, "Bạn không có quyền thực hiện yêu cầu này."),
        new("AUTH_STEP_UP_REQUIRED", 403, "Yêu cầu xác minh lại danh tính."),
        new("RESOURCE_NOT_FOUND", 404, "Không tìm thấy tài nguyên."),
        new("METHOD_NOT_ALLOWED", 405, "HTTP method không được hỗ trợ."),
        new("RESOURCE_CONFLICT", 409, "Tài nguyên bị xung đột."),
        new("AUTH_LAST_ADMIN", 409, "Phải giữ ít nhất một Admin hoạt động."),
        new("AUTH_LAST_LOGIN_METHOD", 409, "Phải giữ ít nhất một phương thức đăng nhập."),
        new("REQUEST_TOO_LARGE", 413, "Nội dung yêu cầu vượt giới hạn."),
        new("UNSUPPORTED_MEDIA_TYPE", 415, "Content-Type không được hỗ trợ."),
        new("RATE_LIMITED", 429, "Quá nhiều yêu cầu. Vui lòng thử lại sau."),
        new("SERVICE_UNAVAILABLE", 503, "Dịch vụ tạm thời không khả dụng.")
    ];
    private static readonly IReadOnlyDictionary<string, ApiError> ByCode = Definitions.ToDictionary(error => error.Code);

    public static ApiError Get(string code) => ByCode.GetValueOrDefault(code) ?? Internal;
    public static ApiError ForStatus(int status) => status switch
    {
        400 => InvalidRequest,
        401 => Get("AUTH_UNAUTHENTICATED"),
        403 => Get("AUTH_FORBIDDEN"),
        _ => Definitions.FirstOrDefault(error => error.Status == status)
            ?? new ApiError("HTTP_ERROR", status, "Yêu cầu không thể hoàn tất.")
    };
}

// Only catalog messages can reach the client. Internal exception messages are never copied.
public sealed class AppException(string errorCode) : Exception("Application operation failed.")
{
    public ApiError Error { get; } = ApiErrors.Get(errorCode);
}
