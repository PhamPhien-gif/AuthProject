# Kế hoạch triển khai Auth

Ngày lập: 09/10/2026. Trạng thái: **P00–P04 đã triển khai, P05–P13 chờ lệnh**.
Kết quả và giới hạn kiểm chứng P01: [báo cáo P01](06-P01-RESULTS.md).
Kết quả và cách mở rộng P02: [báo cáo P02](07-P02-RESULTS.md).
Kết quả persistence, hashing và audit: [báo cáo P03](08-P03-RESULTS.md).
Kết quả chống brute force, lockout và proxy: [báo cáo P04](09-P04-RESULTS.md).
Tập trung Auth; chưa thêm feature nghiệp vụ, giao diện, multi-tenant hoặc microservice.

## 1. Nguyên tắc điều khiển phạm vi

- Chỉ triển khai bước khi có lệnh cụ thể. Các bước bên dưới đi theo thứ tự dependency.
- Mỗi bước có đầu ra, test viết trước, điều kiện hoàn tất; cập nhật trạng thái và bằng chứng sau khi làm.
- TDD: Red (test thất bại đúng lý do) → Green (code tối thiểu) → Refactor → chạy regression.
- Cấu hình/project reference không cần test hình thức; xác minh bằng restore/build/startup.
- Chưa đưa lên production khi chỉ hoàn thành một phần login; gate phát hành nằm ở P13.
- Không chạy migration lên database của người dùng trong giai đoạn lập kế hoạch.

## 2. Thứ tự ưu tiên

| Bước | Ưu tiên | Nội dung | Phụ thuộc | Trạng thái |
|---|---|---|---|---|
| P00 | Nền tảng | Docs, ignore, solution, khung layer/xUnit | — | Đã khởi tạo |
| P01 | Bắt buộc | .NET 10 LTS, dependencies, integration harness | P00 | Đã triển khai; xem báo cáo kiểm chứng |
| P02 | Bắt buộc | DTO validation, lỗi thống nhất, configuration | P01 | Hoàn tất — 76 test pass toàn solution |
| P03 | Bắt buộc | EF/SQL, password hashing, audit nền tảng | P02 | Hoàn tất — 120 test pass sau bổ sung dotenv |
| P04 | Bắt buộc | Rate limiting, lockout, trusted proxy | P03 | Hoàn tất — 155 test pass toàn solution |
| P05 | Auth cơ bản | Email, Register, Verify Email | P04 | Chờ lệnh |
| P06 | Auth cơ bản | Login, JWT, authentication, GetMe | P05 | Chờ lệnh |
| P07 | Auth cơ bản | Rotation, reuse detection, logout/revoke | P06 | Chờ lệnh |
| P08 | Vòng đời tài khoản | Forgot/Reset/Change password | P07 | Chờ lệnh |
| P09 | MFA | Email OTP, enrollment, disable, recovery | P08 | Chờ lệnh |
| P10 | Social login | Google OAuth 2.0/OIDC, linking | P09 | Chờ lệnh |
| P11 | Authorization | RBAC, policy, quản trị user/role | P10 | Chờ lệnh |
| P12 | Quan sát | Login History, AuditLog API, log4net delivery | P11 | Chờ lệnh |
| P13 | Phát hành | E2E, security regression, Swagger, vận hành | P12 | Chờ lệnh |

Đây là thứ tự thực thi mặc định; mức ưu tiên không cho phép bỏ qua dependency.
Authorization nền tảng đã có ở P06; P11 hoàn thiện quản trị và các policy.
Ghi audit bắt đầu ở P03 và được nối vào từng flow ngay khi flow được tạo, không đợi P12.

## 3. Công việc chi tiết

### P00 — Khởi tạo có giới hạn

- [x] Khảo sát solution .NET 8, Swagger và xUnit có sẵn.
- [x] Tổ chức layer bằng folder Controllers, Services, Repositories, Entities, DTOs, Security trong `AuthProject/`.
- [x] Solution có một project ứng dụng AuthProject và project test UnitTestAuth tham chiếu ứng dụng.
- [x] Bỏ WeatherForecast và test rỗng từ template.
- [x] Thêm `.gitignore`, `.editorconfig`, `.env.example`, pin SDK 8 hiện có.
- [x] Viết Docs cho kiến trúc, API, TDD, local và roadmap.
- [x] P01–P04 đã triển khai theo lệnh riêng; P05–P13 chưa thực hiện.

Nghiệm thu: restore/build được; runner xUnit chạy được nhưng chưa có test; ignore giữ lại
source, Docs và file example, loại bỏ output/secrets. Kết quả thực tế xem [local](05-LOCAL-DEVELOPMENT.md).

### P01 — Toolchain và test harness

1. Chuyển tất cả project sang .NET 10 LTS và cập nhật `global.json`, môi trường CI/IDE.
2. Kiểm tra compatibility, pin bản stable được hỗ trợ tại thời điểm thực hiện. EF/JwtBearer/OIDC/
   MVC.Testing cùng major với .NET. Kiểm tra dependency trực tiếp và bắc cầu.
3. Nâng Swagger/xUnit/test SDK/coverage phù hợp; chưa cần chuyển xUnit major nếu không có lợi ích.
4. Tạo project test `AuthProject.IntegrationTests` tham chiếu `AuthProject`, dùng `WebApplicationFactory`.
   Mọi code ứng dụng vẫn nằm trong một project; chỉ test được tách project.
5. Tạo fixture SQL Server test riêng (Testcontainers khi có Docker; SQL Express DB test riêng nếu không).
6. Thiết lập fake clock, fake email transport, fake Google OIDC provider/backchannel, data builders.
7. CI restore → build → unit → integration; không dùng production credentials. Định nghĩa trait Unit/Integration.

Test đầu tiên: host chạy trong Testing, Swagger chỉ có ở Development, API trả 404 với route không tồn tại.
Đây là kiểm tra hành vi môi trường; không tạo test chỉ assert cấu trúc folder.
Nghiệm thu: test harness chạy độc lập, không gửi email/Google thật; test DB không thể trỏ vào `AuthProject`.

Kết quả P01: .NET 10, central package versions + lock files, 14 unit test và 10 integration test
(9 HTTP, 1 SQL Express) pass; build 0 warning/error. CI có nhánh Testcontainers dành cho Linux runner;
chưa chạy trên GitHub trong phiên này. EF/JwtBearer/OIDC production packages được thêm đúng bước
P03/P06/P10, cùng major 10; không cài package chưa sử dụng hoặc triển khai Auth trước phạm vi.
Test Google P01 là fake backchannel; callback/state/PKCE và Auth middleware sẽ kiểm thử ở P10.

### P02 — Hợp đồng chung, validation và lỗi

1. Tạo DTO theo use case tại `DTOs/`, validator tại `DTOs/Validators/`, catalog mã lỗi tại `Exceptions/`.
2. Dùng FluentValidation với explicit async invocation qua filter/service; không dựa vào MVC sync pipeline cho rule async.
3. Thiết lập `ProblemDetails`, `IExceptionHandler`, mapping lỗi nghiệp vụ/không dự kiến.
4. Chuẩn hóa model binding, malformed JSON, validation, 401/403, 404/405, 415, 429 và 500.
5. Tạo typed options + `ValidateOnStart` cho module khi module được bật; thiếu secret không fallback sang giá trị demo.
6. Chuẩn traceId và giới hạn body/request; không log request body chứa credentials.

Red: DTO sai nhiều field; null/body rỗng; JSON hỏng; unexpected exception; lỗi không lộ stack trace;
401 giữ `WWW-Authenticate`, 429 giữ `Retry-After`.
Green: mapping tập trung, không `try/catch` lặp ở controller.
Nghiệm thu: schema trong [API contracts](03-API-CONTRACTS.md) được contract test.

Kết quả P02: 34 unit + 41 HTTP integration + 1 SQL regression pass, 0 failed/skipped;
Release build 0 warning/error. DTOs/validators, shared error writer, async filter,
typed options và request limits đã nối vào pipeline. Không có endpoint Auth thực tế;
auth/rate-limit callbacks sẽ nối vào writer ở P06/P04 khi module được triển khai.

### P03 — Persistence, hashing, audit nền tảng

1. Tạo entity trong `Entities/` cho User, Role/UserRole, AuthSession, RefreshToken, AuthChallenge,
   ExternalLogin, LoginHistory, AuditEvent, OutboxMessage theo schema thiết kế.
2. Tạo DbContext, entity configurations, unique index, rowversion, migration đầu trong `Data/`.
3. Tạo repository theo use case; truy vấn LINQ projection, async, `AsNoTracking` cho read.
4. Adapter `IPasswordHashService` dùng `PasswordHasher<TUser>` chuẩn; versioned hash, random salt, rehash.
5. `IAuditWriter` ghi SQL và outbox cùng transaction với thay đổi bảo mật; adapter log4net xuất event.
6. Viết migration và script review; seed role `User`, `Admin` idempotent, chưa seed admin password.

Red: normalized email unique kể cả concurrent insert; rollback; hash không bằng plaintext;
salt khác nhau; verify/rehash; audit không chứa secret; migration chạy được trên SQL Server test.
Nghiệm thu: chưa cập nhật database local thật; integration test chứng minh constraint/transaction.

Kết quả P03: 44 unit + 45 HTTP/startup + 16 SQL = **105 passed**, 0 failed/skipped;
Release build 0 warning/error. Có 11 bảng Auth, repository LINQ, unit of work, IdentityV3
PBKDF2-HMAC-SHA512 210.000 iterations, audit SQL/outbox Data Protection và log4net adapter.
Migration/script tạo schema rỗng và replay idempotent đã kiểm chứng trên SQL Express test riêng.
Database `AuthProject` chưa được cập nhật; chưa có endpoint Auth hoặc outbox worker.
Chi tiết Red/Green, cấu hình và giới hạn: [kết quả P03](08-P03-RESULTS.md).
Theo yêu cầu sau P03, đã thêm dotenv loader cho Development/EF CLI; regression tăng lên
56 unit + 48 HTTP/startup + 16 SQL = **120 passed**. Không thêm feature P04 hoặc cập nhật DB ứng dụng.

### P04 — Chống brute force trước khi mở login

1. Tạo policy limiter riêng cho login/register, OTP, refresh, recovery, Google và API chung.
2. Partition theo IP đã xác thực qua proxy và keyed hash của normalized account, giới hạn kích thước key.
3. Counter lockout tài khoản lưu SQL và cập nhật atomic; phân biệt với IP rate limiter.
4. Response lỗi đăng nhập chung cho unknown/wrong/locked; dummy hash cho user không tồn tại.
5. Cấu hình trusted proxies/networks; bỏ qua forwarded IP không đáng tin.

Red: request vượt quota → 429; lockout boundary theo fake clock; burst song song không vượt count;
giả `X-Forwarded-For` không đổi partition; account enumeration không lộ qua body/status.
Nghiệm thu: không khóa vô thời hạn; có cooldown; policy triển khai nhiều instance được ghi rõ.

Kết quả P04: **68 unit + 68 HTTP/startup + 19 SQL = 155 passed**, 0 failed/skipped;
Release build 0 warning/error. Limiter fixed-window có giới hạn partition, quota IP/account/session,
429 + Retry-After; SQL lockout và audit/outbox cùng transaction, dummy hash và lỗi credentials chung.
Forwarded headers chỉ được nhận từ proxy/network cấu hình rõ ràng. Migration `AddLockoutFailureWindow`
và script incremental đã có; chưa update DB `AuthProject`. Limiter memory chỉ bảo vệ từng process;
session quota trên anonymous refresh sẽ nối sau khi xác minh token ở P07.
Chi tiết TDD, cấu hình và giới hạn: [kết quả P04](09-P04-RESULTS.md).

### P05 — Email và đăng ký/xác minh email

1. Adapter SMTP (MailKit) và outbox email, template tách riêng; không dùng log để giả gửi email production.
2. Register chỉ nhận email/password/displayName, role mặc định User do server gán.
3. Normalize email, kiểm tra password, hash, insert user pending cùng email outbox/audit.
4. Verify email qua token ngẫu nhiên một lần, lưu digest; endpoint resend có cooldown/quota.
5. Duplicate registration trả generic 202 như đăng ký mới; xử lý unique race không thành 500.

Red: password yếu, role injection, duplicate khác hoa thường, resend, token hết hạn/replay,
SMTP lỗi sau commit không làm mất user, retry outbox không tạo duplicate account.
Nghiệm thu: chưa verified không được tạo phiên; DTO không bao giờ trả PasswordHash/token digest.

### P06 — Login, JWT và GetMe

1. Verify password, trạng thái account, confirmed email; cập nhật counter/rehash đúng transaction.
2. Tạo session và JWT ngắn hạn; chuẩn bị refresh token digest cho P07, không mở flow refresh chưa hoàn tất.
3. JwtBearer kiểm tra signature/issuer/audience/lifetime/algorithm, session active và auth version.
4. Mặc định mọi business endpoint yêu cầu auth; anonymous chỉ whitelist auth flows cần thiết.
5. `GET /api/v1/auth/me`: profile + role hiện tại, không chấp nhận userId do caller chọn.
6. LoginHistory và audit phân biệt login thất bại, primary factor thành công, login hoàn tất.

Red: credentials sai/unknown/locked; disabled/unverified; JWT giả/sai issuer/audience/expired;
revoked session; GetMe không có token → 401; profile không lộ secrets.
Nghiệm thu: chưa phát hành public ở bước này; API refresh/logout phải hoàn tất P07 trước release.

### P07 — Refresh rotation và logout/revoke

1. Refresh opaque CSPRNG, chỉ lưu hash; mỗi lần dùng đổi sang token mới và consume token cũ atomic.
2. Theo dõi token family/session, absolute expiry, idle expiry, replacedBy và revoked reason.
3. Detect token đã consume: revoke cả family/session; commit revoke trước khi trả lỗi.
4. Hai refresh đồng thời: chỉ một consume thành công; request còn lại kích hoạt reuse policy.
5. Logout current/all và revoke session cụ thể, clear cookie đúng flags/path; idempotent đối với phiên đã revoke.
6. Kiểm tra session trên mỗi authenticated request để access token bị vô hiệu ngay sau commit revoke.
7. Triển khai CSRF/Origin validation cho cookie-based refresh/logout và CORS allowlist.

Red: rotation happy path; old-token replay; parallel refresh; refresh vs logout race; expiry;
logout khiến access cũ trả 401; logout-all chỉ tác động đúng user; session người khác không thể revoke.
Nghiệm thu: test concurrency trên SQL Server thật, không dựa trên EF InMemory.

### P08 — Password recovery và thay đổi credentials

1. Forgot password trả generic 202, rate-limit và email outbox.
2. Reset token ngẫu nhiên, một lần, ràng buộc purpose/user/security version/expiry.
3. Reset password update hash + consume token + tăng auth version + revoke sessions atomic.
4. Change password yêu cầu current password và recent authentication; nếu bật MFA phải xác minh factor.
5. Gửi thông báo bảo mật khi password đổi; reset không tự đăng nhập hoặc vô hiệu MFA.
6. Google-only account không tự có password qua forgot-password; thiết lập password là flow riêng có step-up.

Red: token sai purpose, expired, reuse, concurrent reset; password reset chặn access/refresh cũ;
account chưa tồn tại có response giống nhau; MFA vẫn bật sau recovery.
Nghiệm thu: không có đường tắt bypass MFA bằng password reset.

### P09 — MFA Email OTP

1. Enroll bằng recent primary authentication + verify OTP gửi đến email đã xác minh.
2. Login bật MFA chỉ trả challenge; chưa cấp access/refresh/session hoàn tất.
3. OTP CSPRNG 6 chữ số, digest HMAC có secret pepper; challenge bind user/purpose/login attempt.
4. Verify atomic one-time, expiry, failed-attempt cap, resend cooldown và tổng số lần gửi.
5. Resend làm OTP cũ mất hiệu lực, không reset attempt budget hay kéo dài vô hạn challenge.
6. Enable/disable yêu cầu step-up; phát recovery codes một lần và chỉ lưu hash.
7. Login Google cũng đi qua local MFA nếu account bật MFA.

Red: pre-MFA không gọi GetMe được; OTP sai/expired/replay/cross-purpose; song song chỉ một lần dùng;
resend không bypass budget; disable thiếu factor bị chặn; recovery code chỉ dùng một lần.
Nghiệm thu: không gọi Email OTP là MFA chống phishing/AAL2; giải thích giới hạn ở tài liệu kiến trúc.

### P10 — Đăng nhập/đăng ký Google

1. Google OAuth 2.0 Authorization Code + OIDC; dùng middleware chuẩn, PKCE/state/nonce/correlation.
2. Validate issuer/audience/signature/expiry/nonce; identity key là `(provider, sub)`.
3. Tài khoản mới qua Google có role User; chỉ đánh dấu email verified khi evidence provider hợp lệ.
4. Trùng email local: yêu cầu đăng nhập local rồi explicit link với recent auth; không tự merge.
5. Link/unlink có audit, step-up, không cho xóa phương thức đăng nhập cuối cùng.
6. Callback không đưa JWT/refresh vào URL; dùng completion ticket một lần, ràng buộc browser,
   đổi token bằng POST sau redirect về URL allowlist; áp dụng local MFA.
7. Không giữ Google access/refresh token nếu chỉ cần đăng nhập; không xin scope ngoài openid/email/profile.

Red: invalid state/nonce/code replay; canceled consent; provider unavailable; fake email/subject;
account-link takeover; open redirect; callback replay; bypass local MFA.
Nghiệm thu: tự động test bằng fake provider; smoke Google thật là kiểm tra thủ công có credentials riêng.

### P11 — RBAC và quản trị

1. Role User/Admin, user có nhiều role; policy tập trung, không magic string ở controller.
2. User quản lý profile/session/history của mình; Admin quản lý user status/role và xem audit.
3. Gán/gỡ role, disable user tăng auth version và revoke phiên để quyền cũ mất hiệu lực ngay.
4. Chặn self escalation, mass assignment, sửa protected role và xóa/disable/demote Admin active cuối cùng.
5. Bootstrap Admin bằng command vận hành nhận secret ngoài source; idempotent, audit, không public endpoint.
6. Thao tác admin nhạy cảm yêu cầu recent authentication/MFA; enrollment bắt buộc trước khi dùng quyền admin.

Red: role matrix 401/403/200; IDOR; User gửi role=Admin; JWT chứa quyền cũ; concurrent last-admin removal;
Admin thiếu step-up; default role luôn là User.
Nghiệm thu: authorization dựa vào dữ liệu tin cậy, không tin role từ request/client.

### P12 — Login History và AuditLog hoàn chỉnh

1. Query lịch sử cá nhân và audit admin bằng LINQ projection, filter có whitelist, pagination có giới hạn.
2. Hoàn thiện log4net structured fields, RollingFileAppender, retention và quyền đọc file.
3. Worker phát outbox ra log4net; at-least-once, EventId để nhận diện duplicate, retry/backoff/dead-letter/alert.
4. Cleanup token/challenge/history theo retention nhưng giữ tombstone cần phát hiện refresh reuse.
5. Giới hạn PII, mask/shorten IP/UA phù hợp, không ghi credentials/cookies/provider code.

Red: user không đọc history người khác; admin filter/pagination; log redaction;
outbox retry không mất event; disk sink lỗi không khiến Auth state commit nửa chừng.
Nghiệm thu: SQL là bản audit query được; log4net là kênh logging, không tuyên bố file log bất biến.

### P13 — Kiểm thử hệ thống và sẵn sàng vận hành

1. E2E register → verify → login → MFA → GetMe → refresh → logout; Google flow tương ứng.
2. Swagger có JWT bearer, DTO examples, error codes, anonymous/authorized requirements; production tắt mặc định.
3. Kiểm tra TLS, CORS/CSRF, trusted proxy, key rotation, Data Protection key persistence, security headers.
4. Load test hash/OTP/login/rate limiter; kiểm tra quota đa instance và thời gian truy vấn SQL/index.
5. CI bắt buộc unit/integration; dependency audit, secret scan; cấu hình staging tách production.
6. Runbook migration review/backup/restore, signing-key rotation, revoke-all khi lộ token, SMTP outage,
   audit backlog, bootstrap/recovery admin; readiness/liveness không lộ secrets.
7. Viết checklist cấu hình production và quyết định retention dữ liệu trước khi deploy.

Nghiệm thu: toàn bộ regression trong Docs/04 đạt, không còn test security bị skip hoặc TODO;
không có default credential; migration đã kiểm chứng trên test/staging. Việc deploy thật là công việc sau đó.

## 4. Mẫu báo cáo khi hoàn thành một bước

```text
Bước: Pxx | Trạng thái: Done / In progress / Blocked
Đã thay đổi:
Red: tên test + lỗi đúng kỳ vọng trước triển khai
Green/Refactor: hành vi đạt được
Kiểm tra: lệnh, số test pass/fail/skip, môi trường SQL nếu có
Migration/config cần áp dụng:
Giới hạn hoặc quyết định còn mở:
Bước kế tiếp: Pxx (chờ lệnh)
```

Nếu cần dependency ngoài bước được yêu cầu, báo rõ phần thiếu, không tự mở rộng sang feature kế tiếp.
