# Kết quả P08 — Password recovery, change và reauthenticate

Ngày: 10/10/2026. Trạng thái: **Done**. P09–P13 chờ lệnh.

## 1. Phạm vi đã triển khai

- Một project ứng dụng, layer bằng folder: PasswordRecoveryController → IPasswordRecoveryService
  → IPasswordRecoveryStore/EF SQL + Security/Email/Logging adapters. DTO/validator và error catalog chung.
- POST password/forgot: EmailRequest, generic 202, không token/userId/enum account. Active + verified
  + có password mới nhận challenge; Google-only không tự có password. Recovery limiter IP/account;
  SQL cooldown 60 giây, 3 link/900 giây, token 1800 giây mặc định. Forgot concurrent serialize user lock.
- Reset challenge purpose ResetPassword, user/version/expiry/one-time, 32 random bytes canonical Base64URL,
  SHA-256 digest-only SQL. Link mới consume link cũ nhưng không reset send budget.
- POST password/reset: policy DTO, unknown JSON properties reject; token invalid/expired/purpose/version/
  replay hoặc account ineligible chung AUTH_CHALLENGE_INVALID 400. Token không nhận qua GET/query.
- Reset transaction dưới user lock: hash mới + AuthVersion tăng + reset lockout counter + consume tất cả
  outstanding challenges + revoke all sessions/refresh + per-family audit + completion audit/notification.
  Commit rồi 204 + clear refresh cookie, không auto-login. MFA và recovery codes nguyên trạng.
- POST password/change: actor/sid/av từ validated JWT, current password qua credential gate P04 (SQL
  lockout/primary audit), completion kiểm tra lại user/version/session và recent primary/factor.
  New password policy/khác current. Success hash/version/revoke/challenges/audit/notification atomic.
- Recent proof dùng SQL AuthenticatedAt/MfaVerifiedAt, reject future, strict < 300 giây mặc định.
  Refresh không renew proof. Password reauthenticate renew chỉ AuthenticatedAt + Reauthenticated audit,
  204, không token/phiên mới, không đổi MFA/idle/absolute/version. MFA proof missing/expired vẫn 403,
  OTP/Google reauthentication challenge thuộc P09/P10; không fake factor hoặc bypass bằng password.
- Change/Reauthenticate có CookieCsrf khi Refresh bật, quota Login IP và account lockout của gate.
  Forgot/Reset authorize bằng email/token purpose, không dùng ambient refresh cookie để chọn target.
- PasswordEmail outbox kind và Data Protection purpose AuthProject.PasswordEmail.v1, tách verification
  emails P05. Reset link fragment fixed HTTPS; password-changed notification không có credentials.
  Worker lease/rowversion, timeout, retry 30–900 giây, max 10 attempts; check challenge fresh trước send,
  discard expired/replaced/consumed/version mismatch. Notification liên kết audit completion, vẫn gửi
  sự kiện cũ nếu user đổi mật khẩu lần nữa; mailbox phải còn verified/current.
- Module mặc định false; bật cần Persistence/Email và HTTPS ResetUrl/limits. Worker dùng delivery limits
  Auth:Registration kể cả Registration tắt; validate khi recovery bật. Lazy store/gate tránh resolve SQL
  hoặc signing dependencies cho module tắt. Không thêm package/khóa ký hoặc chỉnh credential thực tế.

## 2. Bằng chứng TDD và sửa lỗi

- Viết hai test disabled public endpoints trước code: Forgot/Reset fail 404 thay vì 503. Sau nối
  controller/service/module, 4 test public/protected đều Green (protected routes yêu cầu 401).
- Thêm SQL/HTTP lifecycle, failure/edge/security coverage sau lát cắt endpoint. SQL slice 15/15 và
  HTTP slice 8/8 pass trên DB test thật; concurrent reset chỉ có một winner, old access/refresh bị chặn.
- Startup negative tests phát hiện 6 failure vì options validation chéo gây AggregateException lặp,
  thay vì lỗi options riêng. Đọc module flag cho validation delivery limits loại dependency lặp;
  full startup/HTTP regression Green. Không dùng lỗi permission/môi trường làm Red nghiệp vụ.
- Unit tests kiểm tra primary/factor boundary/future, context protection tách purpose P05/P08,
  link fragment và notification không có reset secret; không test lại thuật toán PBKDF2 của framework.
- Bổ sung concurrency Forgot, Reset-vs-Refresh và notification protection failure rollback.
  SQL giữ user-lock order tương thích P07, không reliance vào EF InMemory hoặc transparent replay.

## 3. Kết quả kiểm chứng

| Suite Release | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit | 121 | 0 | 0 |
| HTTP/startup không SQL | 109 | 0 | 0 |
| SQL-dependent, bao gồm HTTP auth với SQL thật | 139 | 0 | 0 |
| Tổng | **369** | **0** | **0** |

Build solution Release **0 warning, 0 error**. Không đổi entity schema/package versions/lock files.
Enum audit/outbox mới lưu string trong các cột có sẵn; migration regression SQL vẫn pass,
**không có migration mới**, không cần EF update riêng cho P08.
Reports local Git ignored: TestResults/P08/unit.trx, http.trx, sql.trx.

```powershell
dotnet build --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency!=SqlServer'
$env:ConnectionStrings__TestConnection='Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject_Test_Template;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS='LAPTOP-LTSU53RG\SQLEXPRESS01'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency=SqlServer'
```

Phiên này truyền đúng hai biến test bằng TestResults/P08/sql.runsettings local ignored để dùng lệnh
dotnet test trực tiếp với approved prefix. SQL Express Windows identity ngoài sandbox; fixture tạo/drop
riêng AuthProject_Test_<GUID>, không dùng AuthProject/template hoặc DefaultConnection.
HTTP host loại PasswordEmailWorker và không resolve SMTP thật; dispatcher tests dùng fake transport.
Không gửi email thật/Google, đọc/sửa `.env`, update database ứng dụng, commit/push/deploy.

Coverage: Forgot generic response/account state/Google-only; cooldown/budget/replacement/concurrent;
Reset purpose/version/expiry/replay/concurrent; password hash đổi; lockout reset; all challenge/session/
refresh invalidation; MFA/recovery codes giữ nguyên; audit/notification rollback; reset-vs-refresh;
recent proof/reauth and expired factor; current password sai; actor scoping; CSRF/DTO injection;
old access/refresh invalidation/new login/MFA blocking; outbox retry/stale link suppression/notification.

## 4. Cấu hình và giới hạn

Giữ SMTP/Persistence config, JWT/Refresh cho change/reauth, thêm `.env` hiện có rồi restart:

```dotenv
Auth__PasswordRecovery__Enabled=true
Auth__PasswordRecovery__ResetUrl='https://your-client.example/reset-password'
```

ResetUrl là frontend HTTPS do bạn kiểm soát, không query/fragment/userinfo. Frontend chưa triển khai;
email link chứa #token, client POST JSON password/reset. Hướng dẫn Swagger ở
[local mục 14](05-LOCAL-DEVELOPMENT.md#14-bật-password-recoverychangereauthenticate-p08).
Registration flag không cần bật; worker limits chung lấy từ Auth:Registration, mặc định 5/30/120 giây.

SMTP/outbox delivery at-least-once, có thể gửi lặp sau crash; max-attempt pending cần operator điều tra,
requeue đúng message sau sửa nguyên nhân. Worker giữ user lock trong SMTP send để serialize stale-link
check với resend/reset; throughput/cluster/DP keys/ACL/encryption/retention/runbook hoàn thiện P12/P13.
Password gate primary audit/counter commit trước completion transaction như P06; completion rollback
không rollback primary proof event đã commit. Reset không yêu cầu current password vì reset token là proof,
không cấp phiên hoặc tắt MFA. Password reauthenticate không chứng minh factor bổ sung.
MFA OTP/recovery login, Google reauth và setup password cho Google-only chờ bước tương ứng.
Không claim toàn bộ Auth production-ready; P09 chờ lệnh.
