# P10 — Google OAuth 2.0 / OIDC

Trạng thái: hoàn tất triển khai và regression tự động, 10/10/2026. P11–P13 chờ lệnh.
Giữ một project ứng dụng theo folder Controllers/Services/Repositories/Entities/DTOs/Security.

## Đã triển khai

- Microsoft OpenIdConnect 10.0.12: Authorization Code, PKCE S256, state/nonce/correlation,
  metadata/JWKS HTTPS, signature RS256/issuer/audience/lifetime; scopes chỉ openid/email/profile.
- Identity lấy từ validated middleware principal, không request JSON; Google+sub là key.
  User mới active/email verified, role User, không password. Trùng email local yêu cầu explicit link.
- Callback không phát JWT/refresh; redirect fixed completion page với ticket 32 random bytes,
  SHA256 digest SQL, deadline 60s, browser-bound, purpose/version checks, atomic one-use.
  POST complete có CSRF/Origin và protected HttpOnly/Secure/Strict binding.
- Google login vẫn phải local MFA nếu user bật MFA. Pending challenge chưa có phiên/token.
  JWT amr=federated, thêm mfa sau factor; refresh và GetMe dùng SQL session validation hiện có.
- Link/unlink cần bearer actor/session, recent primary/factor; identity không thể chuyển chủ,
  không bỏ login method cuối, thay identity tăng AuthVersion/revoke/audit cùng transaction.
- Google reauthentication phục vụ Google-only: max_age=0 + signed recent auth_time, MFA StepUp
  khi bật; callback đến chậm không làm primary proof cũ trở thành mới.
- Management start trả authorizationUrl JSON cho frontend fetch JWT/CSRF rồi browser navigation.
  Login start giữ 302. Completion 200 login, 202 MFA, 204 link/reauth.
- Không persist Google access/refresh token hoặc external auth cookie; không dùng provider principal
  làm JWT API. Không nhận returnUrl tùy ý hoặc tạo redirect từ Host header.
- Google protocol raw logging bị tắt; safe logger chỉ public error code/server traceId.
  LoginHistory pending/success, audit SQL và protected outbox được nối. Delivery log4net thuộc P12.

## TDD thực tế

GoogleIdentity unit tests viết trước, Red do type chưa tồn tại rồi Green. Năm SQL store tests đầu
viết trước GoogleStore, Red do thiếu implementation rồi Green: role/no session before completion,
email collision, MFA gate, completion browser/expiry và last-login-method protection.
HTTP protocol/startup/concurrency/rollback tests bổ sung sau khi có implementation;
không tuyên bố toàn bộ test được viết trước code.

Ba bug phát hiện bằng test regression và sửa theo Red → Green:

1. Provider error_description có thể bị OIDC middleware log ra raw. Test log collector thất bại
   trước filter, pass sau safe logging. Error redirect không chứa raw provider error/code.
2. Callback reauthentication chậm 301s vẫn chấp nhận auth_time cũ. Test thất bại trước recent-time
   guard, pass sau kiểm tra clock hiện tại và lưu timestamp thực tế từ signed auth_time.
3. Session đã dùng Google rồi reauthenticate bằng password vẫn giữ PrimaryMethod Google.
   Mở rộng test password reauthentication có Red Expected Password/Actual Google; sửa để
   proof SQL và JWT phát hành tiếp theo phản ánh phương thức primary vừa xác minh.

Reports Red/Green và regression tại TestResults/P10 được gitignore; không version artifacts/credentials.
Các case gồm invalid issuer/audience/signature/expiry/state/nonce, code/callback/ticket replay,
PKCE, missing/unverified claims, cancel/provider unavailable, wrong browser, email collision/takeover,
concurrent completion, signing rollback, disabled/version/unlinked identity, MFA và management revocation.

## Migration và cấu hình

Migration `20261010114841_AddPrimaryAuthenticationMethod` thêm varchar(16) PrimaryMethod với default
Password và CHECK Password/Google vào AuthSessions/AuthChallenges. Session/challenge cũ giữ Password;
OTP Password giữ HMAC input P09 để pending OTP tương thích. Google OTP bind thêm primary method.
[003-P10-GooglePrimary.sql](Sql/003-P10-GooglePrimary.sql) là delta idempotent từ schema P04.
SQL test đã chạy migration; chưa update database AuthProject của bạn.

Chạy từ solution root sau khi .env có connection string:

```powershell
dotnet tool restore
dotnet ef database update --project AuthProject --startup-project AuthProject
```

**Cần migration trước startup bản P10 kể cả flag Google=false.** Không tự migrate lúc startup.
[Local mục 16](05-LOCAL-DEVELOPMENT.md#16-bật-google-oauthoidc-p10) có cấu hình Google Console,
fixed callback/completion/error URLs, allowlisted origins, cách viết completion page và thao tác API.
.env.example đã cập nhật; không đọc/sửa .env thật hoặc chèn credentials demo.

## Lệnh kiểm tra

```powershell
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=None'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=SqlServer' --settings <sql-test.runsettings>
```

Runsettings chứa TestConnection và AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS theo hướng dẫn local,
không DefaultConnection. Fixture chỉ create/drop AuthProject_Test_GUID trên server allowlist.
Release regression cả solution dùng runsettings local test, không production credentials.
Kết quả: **140 unit + 141 HTTP/startup + 208 SQL = 489 passed**, 0 failed/skipped.
Release build 0 warning/error, restore --locked-mode pass. So với P09 có thêm 54 test; không thay baseline test thành skip.
Full regression cuối dùng `dotnet test AuthProject.sln --configuration Release --no-restore --settings TestResults/P08/sql.runsettings --logger trx --results-directory TestResults/P10/Final`.
Runsettings local và TRX là artifacts ignored, không phải file cần commit.

## Giới hạn kiểm chứng

Không gọi Google thật hoặc SMTP thật; consent, TLS/cookies trên browser và signed auth_time
của Google reauthentication cần smoke với OAuth client riêng. Thiếu/stale auth_time bị chặn,
không fallback sang callback timestamp. Backend chưa cung cấp frontend completion/error page.
Client phải cùng site với API để cookies Strict hoạt động; khác site cần thiết kế BFF/proxy riêng.
Email OTP không chống phishing/AAL2; Google cũng là mailbox OTP không bảo đảm factor độc lập.
Protocol logger không giữ raw exception/token failure; vận hành dùng traceId/public code.
Rate limiter hiện theo process; quota đa instance, readiness/deploy thuộc P13.
Không triển khai RBAC admin P11, history/audit query/delivery worker P12 hoặc deploy P13.
