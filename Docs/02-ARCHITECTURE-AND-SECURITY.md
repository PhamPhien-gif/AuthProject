# Kiến trúc và thiết kế bảo mật

**Thiết kế dự kiến, chưa phải chức năng đã triển khai.** Các giá trị timeout/quota là baseline
để test, sẽ điều chỉnh bằng đo đạc ở P13. Xem thứ tự triển khai ở [roadmap](01-IMPLEMENTATION-PLAN.md).

## 1. Layered Architecture

Toàn bộ code ứng dụng nằm trong **một project `AuthProject`**, phân chia trách nhiệm bằng folder.
Project test tách riêng và tham chiếu trực tiếp project ứng dụng.

```text
AuthProject.sln
AuthProject/                         Một project ASP.NET Core Web API
  AuthProject.csproj
  Program.cs                         Composition root, DI, HTTP pipeline
  Controllers/                       HTTP binding, gọi service, trả response
  Services/                          Use cases, nghiệp vụ Auth/User/Role/Session
    Interfaces/                      IAuthService, IUserService, IEmailSender, ...
    Email/                           SMTP adapter và templates
  Repositories/                      EF + LINQ, truy vấn và cập nhật dữ liệu
    Interfaces/                      IUserRepository, ISessionRepository, ...
  Entities/                          User, Role, AuthSession, RefreshToken, ...
  DTOs/                              Request/response theo use case
    Validators/                      Validate input DTO, rule tái sử dụng
  Security/                          Password hashing, token, OTP, Google
    Interfaces/                      IPasswordHashService, ITokenService, ...
    Authentication/                  JWT events, session validation
    Authorization/                   RBAC policies, handlers, current-user adapter
  Data/                              DbContext, EF configurations, migrations
    Migrations/                      Schema SQL Server có version
  Exceptions/                        Error catalog, typed failures, ProblemDetails mapping
  Middlewares/                       Exception handler và xử lý HTTP dùng chung
  Filters/                           Validation, endpoint metadata
  Configuration/                     Options và extension đăng ký DI
  Logging/                           Audit writer, log4net adapter/configuration
  BackgroundJobs/                     Outbox delivery, cleanup
UnitTestAuth/                        Project xUnit tham chiếu AuthProject
AuthProject.IntegrationTests/        Test host HTTP + SQL fixture (đã tạo ở P01)
TestSupport/                        Source fake/builder dùng chung, chỉ compile vào project test
Docs/
```

P00 đã tạo sáu folder Controllers, Services, Repositories, Entities, DTOs, Security,
giữ trong Git bằng `.gitkeep`. Các folder bổ sung và folder con trong sơ đồ chỉ tạo khi có code.
Không đặt placeholder service/entity rỗng.

Luồng xử lý: **Controllers → Services → Repositories → Data/DbContext → SQL Server**.
Services sử dụng Security và các adapter Email/Logging qua interface khi cần.
DTOs là hợp đồng input/output; Entities là mô hình dữ liệu và invariant, không trả entity trực tiếp ra API.
Controller không truy vấn DbContext hoặc chứa logic hash/token; repository không quyết định HTTP status.
Service điều phối nghiệp vụ/transaction qua abstraction, không phụ thuộc HttpContext hoặc controller.
Interface nằm cùng folder trách nhiệm tương ứng, implementation được đăng ký bằng DI.
Namespace theo folder, ví dụ `AuthProject.Services`, `AuthProject.Repositories`, `AuthProject.Security`.
Ranh giới layer được giữ bằng quy ước và review trong cùng assembly, không bằng project reference.
Không bắt buộc CQRS/MediatR, không thêm generic repository chỉ để bọc mọi hàm EF.
Repository phục vụ use case và transaction; không trả IQueryable ra API.

## 2. Công nghệ và quyết định nền tảng

| Thành phần | Quyết định |
|---|---|
| Runtime | .NET 10 LTS, SDK 10.0.401 (đã chuyển ở P01) |
| API | ASP.NET Core controllers, async + CancellationToken |
| Persistence | EF Core SQL Server + LINQ; migration được review |
| User store | Entity/domain + EF store riêng, adapter dùng Identity PasswordHasher; không tự viết thuật toán hash |
| Authentication | JWT Bearer + session store; Google qua OAuth 2.0/OIDC |
| Authorization | Role User/Admin, ASP.NET Core policy handlers |
| Validation | FluentValidation explicit async + model binding chuẩn |
| Error | ProblemDetails + catalog code + IExceptionHandler |
| Email | SMTP/MailKit qua IEmailSender; outbox và fake transport khi test |
| Audit | SQL AuditEvent/outbox và log4net RollingFileAppender |
| Tests | xUnit, WebApplicationFactory, SQL Server test thật |
| API docs | Swagger/OpenAPI, bearer scheme, examples, error schema |

Không dùng `MapIdentityApi` bearer token thay JWT: bearer token tích hợp của Identity API
không phải JWT. Hệ thống này phát JWT cho **first-party API trong phạm vi ứng dụng**;
không tuyên bố là OAuth authorization server cho bên thứ ba. Nếu sau này cần federation,
SSO nhiều ứng dụng hoặc third-party clients, thiết kế riêng với identity provider chuẩn.
[Microsoft Identity API](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization)

.NET 8 hết hỗ trợ 10/11/2026; .NET 10 LTS đến 14/11/2028. Đã nâng ở P01
để thống nhất SDK/packages trước khi viết feature; IDE cần hỗ trợ SDK .NET 10 được pin.
[Chính sách hỗ trợ](https://dotnet.microsoft.com/en-us/platform/support/policy)

## 3. Schema dự kiến

ID ngẫu nhiên kiểu Guid, timestamp `DateTimeOffset` UTC, lấy giờ qua TimeProvider.
Các cột dưới đây là tối thiểu, mapping/độ dài cụ thể được chốt khi viết migration P03.

| Entity | Cột chính | Constraint/index quan trọng |
|---|---|---|
| User | Id, Email, NormalizedEmail, DisplayName, PasswordHash nullable, EmailConfirmedAt, Status, MfaEmailEnabled, AuthVersion, FailedLoginCount, LockoutEnd, CreatedAt, UpdatedAt, RowVersion | Unique NormalizedEmail, rowversion |
| Role | Id, Name, NormalizedName, IsSystem | Unique NormalizedName |
| UserRole | UserId, RoleId | PK composite, FK restrictive |
| AuthSession | Id, UserId, AuthVersion, AuthenticatedAt, MfaVerifiedAt, CreatedAt, LastSeenAt, IdleExpiresAt, AbsoluteExpiresAt, RevokedAt, RevokeReason, ClientInfo, RowVersion | UserId + RevokedAt, expiry |
| RefreshToken | Id, SessionId, TokenHash, CreatedAt, ExpiresAt, ConsumedAt, RevokedAt, ReplacedById, RowVersion | Unique TokenHash; SessionId là family key; filtered unique active token/session |
| AuthChallenge | Id, UserId, Purpose, SecretDigest, KeyId, ContextId, Version, CreatedAt, ExpiresAt, Attempts, SendCount, NextSendAt, ConsumedAt, AuthVersion, RowVersion | UserId/Purpose, expiry; purpose/context bắt buộc |
| RecoveryCode | Id, UserId, CodeHash, CreatedAt, UsedAt, RowVersion | Unique CodeHash; atomic consume |
| ExternalLogin | Id, UserId, Provider, ProviderSubject, CreatedAt | Unique Provider + ProviderSubject; mặc định một identity/provider/user |
| LoginHistory | Id, UserId nullable, Method, Outcome, ReasonCode, OccurredAt, IpAddress, UserAgent, SessionId nullable, TraceId | UserId + OccurredAt; user unknown không lưu email plaintext |
| AuditEvent | Id, ActorUserId nullable, SubjectId, EventType, Outcome, ReasonCode, OccurredAt, TraceId, SafeMetadata | OccurredAt, ActorUserId, EventType; append-only trong ứng dụng |
| OutboxMessage | Id, EventId, Kind, ProtectedPayload, CreatedAt, NextAttemptAt, Attempts, ProcessedAt, LeaseUntil, RowVersion | Pending + NextAttemptAt, dedup key theo mục đích |

Không soft-delete mọi bảng một cách máy móc. Audit/history không cascade-delete khi user thay đổi.
Outbox chứa email verify/OTP cần payload nhạy cảm để gửi: mã hóa bằng Data Protection/key store,
giới hạn quyền, không log payload, xóa payload sau gửi/hết hạn. OTP/token trong bảng challenge chỉ có digest.
Persist Data Protection keys ngoài repository; chia sẻ có kiểm soát khi chạy nhiều instance.

Transaction bao gồm state + audit/outbox; email/log sink xử lý sau commit. DbUpdateConcurrencyException
được chuyển thành kết quả use case phù hợp, không trả raw SQL ra API. Nghiệp vụ nhiều row
(refresh/logout, last-admin guard) dùng isolation/lock phù hợp và test interleaving thật.
SQL query dùng LINQ parameterization, projection; paging có limit/order ổn định, tránh N+1 và client evaluation.

## 4. Password và account lifecycle

- Adapter `PasswordHasher<TUser>` dùng format IdentityV3, PBKDF2 có random salt và version marker;
  không SHA256/MD5 plaintext password, không mã hóa hai chiều, không salt cố định.
- P03 kiểm tra PRF thực tế của phiên bản package, cấu hình work factor tương ứng và benchmark.
  Baseline PBKDF2-HMAC-SHA512 210.000 iterations; không nhầm thông số SHA256 với SHA512.
- Verify có `SuccessRehashNeeded` thì rehash khi login thành công; không làm giảm work factor.
- Password mặc định 15–128 ký tự, cho phép spaces/Unicode, không trim hay silently truncate,
  kiểm tra danh sách password phổ biến/bị lộ khi có nguồn phù hợp. Không ép đổi định kỳ vô cớ.
- Email trim + normalize nhất quán để lookup/unique; giữ bản hiển thị; không tự bỏ dấu chấm/plus Gmail.
- Registration trạng thái PendingVerification; chỉ Active + verified mới hoàn tất login.
- Password/reset/email-change/status/role/MFA changes nhạy cảm tăng AuthVersion hoặc revoke session
  theo policy, atomic. Email change chưa nằm trong baseline; thêm sau bằng verify địa chỉ mới + step-up.
- Password reset không chứng minh factor thứ hai và không tắt MFA; trường hợp mất mailbox cần
  recovery code hoặc quy trình hỗ trợ được thiết kế riêng, không thêm endpoint bỏ MFA công khai.

[Identity hashing options](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-configuration),
[OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html).

## 5. JWT, session và revocation

Access JWT baseline 5 phút, clock skew tối đa 30 giây; claims tối thiểu `sub`, `jti`, `sid`,
`iss`, `aud`, `iat`, `nbf`, `exp`, `auth_ver`, role và authentication method khi cần.
Không đưa PII nhạy cảm/password/OTP vào claims. Khóa ký asymmetric RS256, `kid` và allowlist algorithm;
reject alg=none/wrong key/issuer/audience. Secrets và private key ở env/secret store, không source.
Key rotation giữ verification key cũ đến khi token đã phát hết hạn + skew.

Sau kiểm tra JWT cryptographic, lấy session/user từ SQL cho **mỗi request protected**:
session active + user active + auth version khớp. Role mutation tăng version/revoke để JWT role cũ vô hiệu.
DB validation unavailable → fail closed (503 chuẩn), không bỏ qua revocation. MVP không cache kết quả active;
nếu thêm cache sau này phải định nghĩa độ trễ revoke/invalidation đa instance.
Logout có hiệu lực cho request được xác thực sau transaction commit; request đã chạy trước commit
không thể bị thu hồi ngược. Thao tác cực nhạy cảm kiểm tra lại trạng thái trong transaction.

Refresh token: ít nhất 32 random bytes CSPRNG → Base64URL; DB lưu SHA-256 digest, không plaintext.
Hash nhanh phù hợp token entropy cao; không dùng thiết kế này cho OTP/password entropy thấp.
Baseline idle expiry 7 ngày, absolute lifetime session 30 ngày; refresh không kéo dài absolute expiry.

Rotation transaction:

1. Lookup token hash, lock session và token theo thứ tự ổn định; check account/version/expiry/revocation.
2. Nếu token active: conditional consume chỉ khi chưa consumed/revoked và rowversion đúng.
3. Insert successor, đặt ReplacedBy, cập nhật session idle expiry, audit; commit rồi mới trả token.
4. Nếu token đã consumed: revoke session + toàn family, lưu audit **và commit**; sau đó trả 401.
   Không throw trước commit khiến transaction rollback mất dấu revoke.
5. Token ngẫu nhiên không tồn tại chỉ trả generic invalid; không suy đoán family của user.

Mặc định strict reuse: hai refresh song song có tối đa một response success, request còn lại
revoke family; token mới của request thắng cũng không còn dùng được sau revoke commit.
Client phải single-flight refresh. Retry sau network timeout có thể yêu cầu login lại;
không mở grace window khi chưa có thiết kế idempotency chống replay.
Refresh-vs-logout cùng khóa session để không sinh successor active sau revoke.
Giữ record consumed đến absolute session expiry + retention điều tra; cleanup không phá reuse detection.
[RFC 9700, refresh token rotation](https://www.rfc-editor.org/rfc/rfc9700.html#section-4.14).

## 6. Browser transport, CSRF và CORS

Baseline cho browser cùng site: access token trong response và memory của client;
refresh trong cookie HttpOnly + Secure + SameSite=Strict + Path=/api/v1/auth, không Domain.
Tên cookie có thể dùng prefix `__Secure-`; không dùng `__Host-` với Path khác `/`.
Không localStorage và không URL query cho token. Response token dùng `Cache-Control: no-store`.

Cookie dùng trên refresh/logout tạo nhu cầu CSRF: anti-forgery token bound browser context,
header riêng và kiểm tra Origin allowlist cho state-changing endpoints; CORS không tự thay thế CSRF.
Có endpoint lấy CSRF request token; chỉ exact allowed origins, không wildcard + credentials.
OAuth correlation cookie tách riêng, SameSite=None + Secure khi middleware yêu cầu callback cross-site.
Nếu frontend khác site, phải chốt cookie/CSRF/CORS và kiểm thử browser trước thay đổi SameSite.
Native client/BFF transport là extension sau, chưa bật hai transport cạnh tranh trong baseline.

## 7. Email OTP

Theo yêu cầu, cung cấp bước xác minh bổ sung qua email sau password/Google. Email OTP phụ thuộc
bảo mật mailbox và không chống phishing; nếu Google account cũng là mailbox nhận OTP thì hai bước
không độc lập hoàn toàn. Không quảng bá flow này đạt NIST AAL2: NIST không chấp nhận email cho
out-of-band authentication. Có thể mở rộng TOTP/WebAuthn sau bằng interface factor provider.
[NIST SP 800-63B](https://pages.nist.gov/800-63-4/sp800-63b.html).

- OTP 6 chữ số random cryptographic; giữ dạng string để không mất số 0 đầu.
- HMAC-SHA256 với server pepper riêng, keyId + challengeId + purpose + version + OTP;
  constant-time comparison. Secret pepper ngoài DB/repository, có kế hoạch rotation.
- Mỗi OTP hết hạn 5 phút; challenge tổng tối đa 10 phút; tối đa 5 lần verify sai/challenge,
  resend cách 60 giây và tối đa 3 lần gửi/challenge; rate-limit theo user/IP bổ sung.
- Resend thay code/version, giữ total failed attempts và absolute challenge deadline.
- Bind challenge vào user/purpose/pre-auth context; không nhận userId/email để đổi đối tượng khi verify.
- OTP consume + tạo session + audit atomic; response mới có token khi factor hoàn tất.
- Pre-auth ticket opaque random, giới hạn mục đích, không được JwtBearer nhận làm access token.
- MFA enrollment cần recent primary auth ≤ 5 phút và OTP; disable cần current factor/recovery code.
- Recovery codes entropy cao, hiển thị một lần, lưu hash, atomic one-time; regenerate vô hiệu bộ cũ.

## 8. Google OAuth 2.0 / OpenID Connect

OAuth cấp quyền, OIDC xác minh danh tính. Dùng Authorization Code + PKCE S256, state, nonce,
exact redirect URI, backchannel TLS và metadata/JWKS được middleware kiểm tra.
Identity bền vững là Google `sub`, không email. Validate email_verified khi dùng để xác minh địa chỉ.
Không nhận email/avatar/role client gửi như bằng chứng đăng nhập.

Callback tìm ExternalLogin; nếu chưa có thì tạo User hoặc yêu cầu link explicit nếu email đã tồn tại.
Link bind current authenticated user + recent auth + OAuth state có purpose Link; không ghi đè identity
đã thuộc user khác. Login/link callback không phát token qua redirect query/fragment.
Completion ticket một lần ≤ 60 giây bind browser correlation; redeem bằng POST kiểm tra CSRF/origin,
rồi cấp token hoặc challenge local MFA. Chỉ lưu safe return path đã allowlist từ lúc bắt đầu.
[Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect).

## 9. Rate limiting và lockout

Baseline configurable: login 10 request/phút/IP và 5 password failure/account/15 phút → khóa 15 phút;
register/forgot/resend 5 request/15 phút/IP, email send 3/15 phút/account;
OTP 5 attempt/challenge + 10 verify/phút/IP; refresh 30/phút/session và 60/phút/IP;
Google start/completion 10/phút/IP. Các limiter xếp lớp, không dùng quota IP thay account counter.

ASP.NET Core limiter bảo vệ process; lockout SQL shared. Nhiều instance cần distributed limiter/edge
shared quotas ở P13, không tuyên bố limiter in-memory bảo vệ toàn cluster. Giới hạn memory partitions,
header/body/key length để attacker không tạo state vô hạn. Login response giống nhau cho account không
tồn tại/sai password/lockout; logs nội bộ phân biệt reason nhưng không trả cho client.
Kiểm tra timing bằng benchmark, không hứa timing tuyệt đối giống nhau trên mạng.
[OWASP Authentication](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html).

## 10. Audit và vận hành

Events: registration/verification, password login failure/success, MFA challenge/failure/success,
refresh rotated/reuse, logout/revoke, recovery/password changed, role/status mutation,
Google login/link/unlink, access denied và lockout. Login pending MFA chưa tính success hoàn tất.
Audit chứa eventId, actor, target, outcome, reasonCode, UTC, traceId và safe metadata;
không password, JWT, refresh, OTP, reset token, cookies, Authorization header, OAuth code/client secret.

SQL audit/outbox cùng transaction cho state changes. Nếu SQL/audit durable write thất bại thì rollback;
file sink tạm lỗi thì retry sau, không rollback state đã commit. Worker có lease để tránh nhiều consumer
cùng xử lý; log delivery at-least-once, eventId giúp deduplicate. Giám sát disk full/outbox backlog.
Không chỉ gọi `ILog.Info` rồi tuyên bố audit durable: log4net sink có thể báo lỗi nội bộ.
[Apache log4net appenders](https://logging.apache.org/log4net/manual/configuration/appenders.html).

Baseline retention dự kiến: login history 90 ngày, audit 365 ngày, local file 30 ngày theo size/time;
chốt theo yêu cầu vận hành trước production. Quyền đọc/sửa file và audit DB được giới hạn;
không tuyên bố append-only application tương đương tamper-proof storage.

## 11. Những phần chưa nằm trong baseline

UI/frontend; multi-tenant; permission catalog động; OAuth server cho bên thứ ba; TOTP/passkey;
SMS OTP; SSO doanh nghiệp; impersonation; account deletion/export; change email;
dịch vụ hỗ trợ thủ công lấy lại account khi mất toàn bộ factor. Thiết kế extension points đủ nhỏ,
chưa tạo abstractions chỉ để dự phòng các feature này.
