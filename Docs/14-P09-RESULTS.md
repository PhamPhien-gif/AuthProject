# P09 — MFA Email OTP

Đã triển khai trong một project ứng dụng theo folder layer. Không thêm package hoặc migration: schema P03 đã có challenge, recovery code và các timestamp MFA/session. Google/OIDC chưa triển khai; P10 phải nối primary authentication của Google vào cùng policy local MFA trước khi cấp phiên.

## Hành vi

- Login của user bật MFA trả 202 challenge + cookie binding, chưa tạo session/access/refresh. OTP hoặc recovery code đúng mới cấp phiên. Module tắt vẫn chặn tài khoản bật MFA.
- Enrollment cần JWT actor, email verified và primary proof SQL recent; OTP đúng mới bật flag, tăng AuthVersion, revoke mọi session/refresh và consume challenges. Trả recovery codes một lần, không auto-login.
- Step-up yêu cầu password qua gate/lockout P04 rồi OTP hoặc recovery code. Chỉ completion mới renew primary/factor proof SQL; không renew idle/absolute lifetime hoặc cấp token mới.
- Disable/regenerate cần cả hai proof recent; invalidate codes cũ, version++, revoke toàn bộ phiên/challenge. Reset password P08 vẫn giữ MFA/recovery.
- OTP 6 ASCII digits CSPRNG, HMAC-SHA256 với pepper riêng, bind user/challenge/context/purpose/version/AuthVersion/key ID. Chỉ digest ở challenge; raw code chỉ trong outbox mã hóa và email.
- Mặc định OTP 300s, challenge deadline cố định 600s, 5 lần sai, cooldown 60s, 3 lần gửi/challenge và tổng 3 lần/user trong window 600s (kể cả consumed). Resend thay code, giữ attempts/CreatedAt, không kéo dài deadline. Recovery verification dùng deadline challenge, không phụ thuộc TTL của OTP email.
- User lock trước mọi mutation, transaction chung cho OTP/recovery consume, session/token, audit/outbox. Ký JWT lỗi rollback. Verify song song chỉ một winner.
- Cookie `__Secure-AuthProject.Mfa` HttpOnly/Secure/Strict/path `/api/v1/auth`, Data Protection purpose riêng, không dùng làm authentication. POST cần origin allowlist + CSRF; management cần bearer đúng actor/session/version. OTP/cookie không nằm trong response/token/log.
- JWT phát từ phiên MFA có `amr: ["pwd", "mfa"]`, refresh giữ bằng chứng này; authorization vẫn kiểm tra trạng thái SQL. `mfa` là bằng chứng tổng quát, không khẳng định email OTP hay recovery là phương thức chống phishing.
- Worker MfaEmail riêng, lease/rowversion, bounded timeout/retry/backoff. Kiểm tra version/digest/user/purpose/expiry trước SMTP; discard OTP replaced/consumed. Log chỉ message ID và loại lỗi. Delivery at-least-once; cleanup/retention và multi-key rotation thuộc P12/P13.

## Kiểm chứng

Test OTP primitive được viết trước implementation: Red do thiếu EmailOtpSecret → Green 6 case. Test JWT nhiều `amr` Red do validator cũ chỉ nhận một claim → Green. SQL/HTTP acceptance được bổ sung sau implementation; không coi toàn bộ P09 là một chu trình test-first đầy đủ. Lần chạy SQL đầu lỗi cách cô lập protector của test, đã sửa query lấy đúng payload theo user; không nới lỏng bảo vệ runtime.

Rà soát cửa sổ gửi: viết SQL test trước bản sửa, Red khi challenge mới bỏ qua resend gần đây của challenge cũ → đổi account budget sang đếm durable MfaChallenged audit theo thời điểm từng email được stage → Green. Các report `send-window-red.trx`/`send-window-green.trx` giữ bằng chứng này. Audit retention/cleanup phải giữ các event ít nhất hết send window; không đếm theo CreatedAt của challenge.

Test barrier SQL + fake clock cũng được viết trước bản sửa expiry: Red khi verify lấy timestamp trước lúc chờ User lock → đọc thời gian sau khi giữ lock cho verify/resend/management → Green. Reports `expiry-lock-red.trx`/`expiry-lock-green.trx`; không sleep để làm OTP hết hạn.

Release regression: **132 unit + 130 HTTP/startup + 173 SQL = 435 passed**, **0 failed/skipped**. Build solution **0 warning/error**. Trong đó 66 test mới: 11 unit, 21 HTTP/startup, 34 SQL; SQL gồm concurrent OTP/resend/recovery across login & step-up, expiry/cross-purpose/context/version/key/user state, enrollment/disable/regeneration, signing/audit rollback, stale outbox và CSRF/DTO/quota HTTP.

Báo cáo TRX ignored tại `TestResults/P09/unit.trx`, `http.trx`, `sql.trx`; targeted reports `p09-mfa.trx`, `p09-http.trx` là các lần chạy trước regression cuối. Commands từ solution root:

```powershell
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency!=SqlServer'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration&Dependency=SqlServer'
```

SQL command cần TestConnection + server allowlist (local mục 6), hoặc Docker; không dùng DefaultConnection. Phiên này truyền hai biến test bằng ignored `TestResults/P08/sql.runsettings` qua `--settings`, dưới identity có quyền Windows SQL. Test SMTP dùng transport giả; SQL fixture chỉ create/drop `AuthProject_Test_<GUID>` trên server allowlist. Không update database ứng dụng, thay `.env`, gửi email thật hoặc triển khai P10. CI/cloud/SMTP thực chưa kiểm chứng trong phiên này.

Cấu hình và flow thao tác: [local mục 15](05-LOCAL-DEVELOPMENT.md), [.env.example](../.env.example). Email OTP không chống phishing và không được coi là NIST AAL2; giới hạn này giữ nguyên trong [kiến trúc](02-ARCHITECTURE-AND-SECURITY.md).
