# Kết quả P07 — Refresh rotation, reuse, logout và session

Ngày: 10/10/2026. Trạng thái: **Done**. P08–P13 chờ lệnh.

## 1. Đã triển khai

- Giữ một project ứng dụng, layer bằng folder. Controllers → Services → ISessionStore/Repositories
  → EF SQL Server/LINQ; security helpers, typed options, DTO/validator và lỗi qua catalog chung.
- Login trả AuthResult như P06, chuyển secret qua SessionGrant nội bộ để ghi refresh cookie khi
  Auth:Refresh:Enabled=true. Secret CSPRNG 32 bytes, Base64URL canonical 43 ký tự; SQL chỉ giữ SHA-256.
  Cookie `__Secure-AuthProject.Refresh`: host-only, HttpOnly, Secure, SameSite=Strict, Path=/api/v1/auth.
  Khi module tắt, Login giữ chế độ access-only P06, không phát cookie.
- POST Refresh lookup hash, lock User → AuthSession, kiểm tra lại account/version/MFA/expiry/revoke,
  consume token cũ, insert successor, gán ReplacedById, idle/LastSeenAt + audit/outbox cùng transaction.
  Các SaveChanges tuần tự giải phóng filtered unique active index và giữ self-FK hợp lệ trước commit.
  Signing hoặc audit failure rollback; không transparent replay transaction/rotation.
- Session là token family. Token consumed được giữ làm tombstone; reuse của family còn active
  revoke session và toàn bộ token, stage RefreshReused/SessionRevoked + protected outbox, commit trước 401.
  Reuse vẫn phát hiện sau expiry cũ khi family được refresh kéo dài; xử lý trước quota session.
- Parallel refresh có tối đa một success; loser kích hoạt strict reuse và vô hiệu successor/access.
  Refresh-vs-logout cùng user/session lock; không để lại active successor sau logout commit.
- Idle mặc định 7 ngày, sliding chỉ tại refresh success, absolute 30 ngày không kéo dài;
  JWT/cookie expiry cap theo session. AuthenticatedAt/AuthVersion không được refresh thành recent auth.
- Logout current dùng cookie, kể cả access hết hạn; absent/invalid/already revoked cookie vẫn 204
  khi CSRF hợp lệ, clear đúng flags/path. Logout-all cần JWT, tăng user AuthVersion + revoke mọi family
  của actor, chặn stale password acceptance hoàn tất sau logout-all.
- GET Sessions phân trang actor từ sub, LINQ projection/order ổn định, pageSize 1–100, totalCount.
  DELETE owner session: unknown/non-owner cùng 404, revoke lại idempotent, clear cookie khi current session.
  Guid route DTO được model binding/FluentValidation kiểm tra; service giữ invariant cho caller nội bộ.
- JwtBearer P06 vẫn kiểm tra SQL mỗi request: access cũ bị từ chối ngay ở validation sau revoke commit.
- ASP.NET Core IAntiforgery + Data Protection: GET csrf trả requestToken và cookie context
  `__Host-AuthProject.Csrf`, Path=/, HttpOnly/Secure/Strict. Header X-CSRF-TOKEN cho Login/Refresh/
  Logout/Logout-all/DELETE; Origin exact configured HTTPS, Referer origin fallback nếu Origin absent.
  CSRF context độc lập bearer identity, actor được khôi phục sau validation; không cần access còn hạn.
- CORS exact allowlist + credentials, không wildcard. Startup validate Refresh bật phải có JWT/origins.
  Default false; disabled public session routes trả safe 503 mà không resolve SQL/signing dependencies.
- Refresh IP quota ở middleware; quota session chỉ sau verified digest/session lookup, không partition
  bằng cookie chưa tin cậy. 429 giữ Retry-After và token/cookie; dùng metadata tránh double count bearer sid.
- Lỗi nghiệp vụ/exception dùng ProblemDetails chung. ApiProblemResult kế thừa ActionResult để trả
  typed response hoặc error mà giữ Set-Cookie/Retry-After. Swagger thêm CSRF header và giữ anonymous metadata.

## 2. Bằng chứng Red → Green

- Viết RefreshDisabledTests trước endpoint: 3 test fail đúng kỳ vọng với 404 thay vì 503.
  Khi nối DI, test tiếp tục bắt 500 do resolve persistence khi module tắt; lazy store giải quyết,
  cả 3 Green, không cần SQL/key cho disabled routes.
- RefreshTokenSecretTests viết trước helper, runner dừng vì type chưa có; sau implementation,
  kiểm tra entropy/uniqueness/canonical shape/digest và invalid input đều pass. Đây là compile Red,
  không dùng nó làm bằng chứng concurrency hoặc bảo mật flow.
- SQL/HTTP slice bắt lỗi header: 25/27 pass, replay lỗi thiếu Set-Cookie và quota thiếu Retry-After
  do exception pipeline clear response. Trả ApiProblemResult từ typed outcome giữ header, 27/27 Green.
- Refactor Lazy<ISessionStore>, đưa lifetime rule về Security, thêm edge tests audit rollback sau
  consume, tombstone quá expiry, quota-full reuse, absolute cap, route validation và Swagger contract.
- Sandbox SQL không kết nối được named instance (error 26); đây là lỗi môi trường, không tính Red
  nghiệp vụ. Chạy ngoài sandbox bằng Windows identity trên DB test riêng đã kiểm chứng SQL thật.

## 3. Kiểm tra Release

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit | 110 | 0 | 0 |
| HTTP/startup, không SQL | 98 | 0 | 0 |
| SQL-dependent, gồm HTTP auth với SQL thật | 113 | 0 | 0 |
| Tổng | **321** | **0** | **0** |

Build solution Release: **0 warning, 0 error**. Không thêm package, thay lock file hoặc entity schema.
Reports local, Git ignored: TestResults/P07/unit.trx, http.trx, sql.trx.
Swagger filter thêm cuối bước đã được build và kiểm tra bằng HTTP Swagger contract; HTTP suite chạy lại 98 pass.

Lệnh từ solution root:

```powershell
dotnet build --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency!=SqlServer'

$env:ConnectionStrings__TestConnection='Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject_Test_Template;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS='LAPTOP-LTSU53RG\SQLEXPRESS01'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency=SqlServer'
```

Trong phiên dùng runsettings local ở TestResults/P07/sql.runsettings để truyền đúng hai biến test
và chạy lệnh dotnet test trực tiếp, cho phép permission prefix có thể dùng lại. File này không commit.
Fixture luôn tự tạo/drop AuthProject_Test_<GUID>, revalidate tên trước drop; không fallback DefaultConnection,
không kết nối hoặc update DB AuthProject/template. Không gửi SMTP/Google thật, không sửa `.env`.

Coverage P07: happy rotation; old-token reuse + successor invalidation; parallel refresh;
refresh/logout race; signing/audit rollback; idle/token/absolute expiry; disabled/unverified/version/MFA;
absolute cap; tombstone retention; logout-all scope/stale acceptance; owner list/revoke/idempotency;
expired access refresh; cookie flags/no JSON secret; CSRF missing/wrong/cross-context; origin/CORS;
invalid/missing cookie; verified session quota/cooldown/Retry-After; startup và Swagger.

## 4. Bật local và giới hạn

Không có migration mới; dùng schema P03/P04 đã có. Không cần EF update riêng cho P07.
Giữ JWT/Persistence/RSA config, thêm vào `.env` hiện có rồi restart:

```dotenv
Auth__Refresh__Enabled=true
Auth__Refresh__AllowedOrigins__0='https://localhost:7148'
```

GET csrf trước Login, giữ context cookie, gửi requestToken qua X-CSRF-TOKEN.
Hướng dẫn browser/Swagger và .env.example: [local mục 13](05-LOCAL-DEVELOPMENT.md#13-bật-refreshlogoutsessionscsrf-p07).

Strict reuse yêu cầu client single-flight; timeout mất response sau commit có thể buộc login lại,
không có grace/idempotency window. Cookie Strict dành browser cùng site; WebApplicationFactory
không kiểm chứng toàn bộ browser cookie/TLS/proxy policy, cần smoke test deployment ở P13.
Rate limiter vẫn in-memory theo process; multi-instance quotas/shared DP keys/ACL/encryption/retention
chờ P12/P13. Cleanup chưa chạy, nên chưa xóa tombstone sớm. Audit SQL/outbox đã atomic, log4net delivery
worker vẫn P12. Account MFA tiếp tục bị chặn step-up đến P09; Google/password recovery/RBAC admin chưa mở.
Request đã được xác thực trước commit revoke không thể thu hồi ngược; các request validate sau commit bị chặn.
Logout-all tăng AuthVersion; future challenge flows cần tôn trọng version/expiry như thiết kế P08–P10.

P08 chờ lệnh. Chưa deploy hoặc tuyên bố toàn bộ hệ thống Auth đã sẵn sàng production.
