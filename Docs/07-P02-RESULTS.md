# P02 — DTO validation, lỗi thống nhất và cấu hình

Ngày: 09/10/2026. Trạng thái: **hoàn tất P02**. P03 và các bước nghiệp vụ tiếp tục chờ lệnh.

## 1. Đã triển khai

| Folder/file | Trách nhiệm |
|---|---|
| `DTOs/AuthRequests.cs` | Request register/login/email/token/password/OTP/recovery/Google |
| `DTOs/QueryRequests.cs` | Paging/history, gán role và trạng thái user |
| `DTOs/Validators/` | FluentValidation rules, rule email/password/token dùng chung |
| `Exceptions/ApiErrors.cs` | Catalog mã lỗi, HTTP/type/title, AppException có code an toàn |
| `Exceptions/ApiProblems.cs` | Factory, writer, IActionResult và IProblemDetailsWriter dùng chung |
| `Filters/AsyncValidationFilter.cs` | Resolve validators qua DI, await ValidateAsync trước controller |
| `Filters/ApiErrorResultFilter.cs` | Chuẩn hóa error body tùy ý từ controller |
| `Middlewares/ApiExceptionHandler.cs` | IExceptionHandler, mapping lỗi dự kiến/bất ngờ, log an toàn |
| `Middlewares/RequestFoundationMiddleware.cs` | Trace header/scope, giới hạn body kể cả streaming |
| `Configuration/` | Typed options, ValidateOnStart, đăng ký nền tảng API/request timeout |
| `Program.cs` | Nối các thành phần vào pipeline; Swagger vẫn chỉ Development |

Code vẫn nằm trong một project ứng dụng. Thêm dependency
`FluentValidation.DependencyInjectionExtensions` 12.1.1, cập nhật cả ba lock files.
Không dùng package `FluentValidation.AspNetCore` hoặc sync auto-validation.

## 2. Hành vi chính

- Request DTO tách entity; security DTO từ chối unknown JSON properties để chặn mass assignment.
- Validator trả code/message theo field, không echo password/token; không thay đổi password input.
- Email/name được kiểm tra với giá trị trim, nhưng normalization/lưu dữ liệu thuộc service P05.
- Login chấp nhận password legacy ngắn; chính sách 15–128 chỉ áp dụng password mới.
- OTP giữ số 0 đầu, đúng 6 ASCII digits, không chấp nhận Unicode digits/newline/space.
- Recovery code dự kiến 32 random bytes → 43 Base64URL characters; chưa có generator/verify ở P02.
- History query nhận from/to cùng nhau, UTC, tối đa 90 ngày; sort allowlist.
- Invalid JSON/body rỗng/null/query parse sai → 400 `INVALID_REQUEST` với field `request` generic.
- FluentValidation failure → 400 `VALIDATION_FAILED`, errors theo field camelCase.
- 401/403/404/405/413/415/429/500/503 dùng ProblemDetails; giữ WWW-Authenticate/Retry-After/Allow.
- Unexpected exception luôn 500 generic kể cả Development hoặc Accept text/html.
- Error có `X-Trace-Id`, JSON traceId cùng giá trị, instance chỉ path, Cache-Control no-store.
- `IProblemDetailsService` cũng dùng writer an toàn; không xuất arbitrary detail do caller truyền vào.
- Log exception chỉ có errorCode/type/traceId; không log object exception, message, stack, Data hoặc body.
- Body limit mặc định 16 KiB; request timeout mặc định 15 giây → 503 nếu pipeline còn có thể viết response.

Giới hạn áp dụng cho request đi vào pipeline. Kestrel/proxy từ chối trước pipeline, client disconnect
hoặc response đã bắt đầu không đảm bảo viết lại JSON. Timeout dùng middleware ASP.NET Core chuẩn;
khi debugger attach, middleware có thể không áp timeout theo hành vi framework.

## 3. Cách mở rộng ở bước tiếp theo

### DTO và validator

Thêm request vào `DTOs/` và `AbstractValidator<T>` vào `DTOs/Validators/`.
DI tự scan assembly; `[ApiController]` action nhận DTO sẽ chạy validator qua async action filter.
Rule async dùng `MustAsync`/`CustomAsync`, nhận CancellationToken; không gọi `.Validate()` sync.
Service được gọi ngoài HTTP phải chủ động inject `IValidator<T>` và await `ValidateAsync`,
vì action filter chỉ bảo vệ HTTP boundary. Invariant/unique/ownership vẫn kiểm tra tại service/DB.

Validation message phải là text công khai, không dùng placeholder `{PropertyValue}` cho secret.
Không dùng DTO validation làm bằng chứng token còn hạn, OTP đúng hoặc actor được phép thao tác.

### Lỗi nghiệp vụ

Thêm mã vào catalog `ApiErrors`, dùng message cố định. Tại HTTP boundary:

```csharp
var problem = problems.Create(HttpContext, ApiErrors.Get("RESOURCE_CONFLICT"));
return new ApiProblemResult(problem);
```

Khi cần unwind qua exception, `throw new AppException("RESOURCE_CONFLICT")` được map tập trung.
Code không có trong catalog trở thành `INTERNAL_ERROR`, không đưa arbitrary message ra client.
Tránh `BadRequest(new { ... })` cho lỗi có code riêng: result filter sẽ chuyển nó thành lỗi generic
theo status. Chỉ `ApiProblemResult` giữ cấu trúc/code do factory tạo.

JWT challenge/forbid và limiter OnRejected ở P06/P04 gọi `ApiProblems.WriteAsync` sau khi đặt
headers protocol cần thiết. P02 mới kiểm chứng hợp đồng 401/403/429 và việc giữ headers,
chưa triển khai JWT authentication hoặc quota/brute-force protection.

### Typed options

| Section | Khi Enabled=true phải có |
|---|---|
| `Auth:Jwt` | Issuer, Audience, SigningKeyPath, KeyId; access lifetime 60–900s, skew 0–30s |
| `Auth:Email` | Host, Port 1–65535, UseTls=true, Sender hợp lệ, Username, Password |
| `Auth:Google` | ClientId, ClientSecret, CallbackPath nội bộ bắt đầu `/`, không `//`, query hay fragment |
| `Auth:EmailOtp` | Email.Enabled=true, Pepper Base64 ít nhất 32 bytes, KeyId, expiry 60–600s, attempts 1–10 |

Mọi module mặc định disabled, không secret mẫu fallback. P02 xác minh shape/required fields;
chưa mở key file hoặc kết nối SMTP/Google. Đọc key/kiểm tra thuật toán, credential thực và sự sẵn sàng
của provider phải bổ sung khi module được triển khai. `Enabled` hiện chỉ điều khiển validation,
không tự tạo endpoint hoặc kích hoạt authentication. Options được validate lúc startup;
đổi cấu hình cần restart process.

## 4. Bằng chứng TDD và kiểm tra

| Giai đoạn | Kết quả |
|---|---|
| Red DTO validation | 16 failed, 4 positive controls passed trước khi thêm rules |
| Red hợp đồng HTTP | 16 failed trước khi nối shared pipeline |
| Red startup options | 6 failed vì cấu hình sai chưa bị từ chối |
| Red ad-hoc controller error body | 1 failed trước khi thêm ApiErrorResultFilter |
| Green unit suite (P01 + P02) | 34 passed, 0 failed, 0 skipped |
| Green HTTP integration (P01 + P02) | 41 passed, 0 failed, 0 skipped |
| SQL Express regression P01 | 1 passed, fixture tạo/xóa catalog GUID riêng |
| Release build | 0 warning, 0 error |
| Locked restore | Thành công |
| NuGet direct/transitive audit | Không thấy vulnerable package theo nguồn hiện tại |

Tổng **76 test pass**. Reports TRX: `TestResults/P02/Unit`, `TestResults/P02/Http`, `TestResults/P02/Sql`.
Không tính lỗi compile ban đầu hoặc lỗi quyền Event Log/TLS của sandbox là Red nghiệp vụ.

Các test bổ sung kiểm chứng rule async thực sự được await, byte limit khi không biết Content-Length,
timeout cancellation, framework ProblemDetails writer, log redaction và endpoint probe không xuất hiện
trong real host/Swagger. Test host dùng RecordingLoggerProvider trong memory, không cần quyền Windows
Event Log. Test controller chỉ được AddApplicationPart từ project test, không nằm trong API assembly.

## 5. Trạng thái workspace và giới hạn

- Git root được chuyển vào thư mục solution trước lượt P02. Đã thêm `.gitignore` và đặt workflow
  `.github/workflows/auth-ci.yml` tại root hiện tại, sửa working directory/cache/artifact paths tương ứng.
  Không reset/stage/commit các thay đổi cấu trúc Git có sẵn của người dùng; không đọc nội dung `.env`.
- CI/Docker chưa chạy trên GitHub trong phiên này; local HTTP/unit và SQL Express đã chạy.
- Chưa có register/login/MFA/Google flow, DB entity/migration hay logging adapter log4net.
- P03 là bước kế tiếp: EF/SQL persistence, password hashing và audit nền tảng, chờ lệnh.

Nguồn đối chiếu: [FluentValidation manual async validation](https://docs.fluentvalidation.net/en/latest/aspnet.html),
[ASP.NET Core exception handling](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).
