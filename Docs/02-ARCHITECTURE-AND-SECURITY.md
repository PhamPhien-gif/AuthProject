# Kiến trúc và thiết kế bảo mật

**Thiết kế toàn hệ thống; P00–P10 đã triển khai, gồm password recovery, MFA Email OTP và Google/OIDC; quản trị RBAC và delivery audit chờ bước tiếp theo.** Các giá trị timeout/quota là baseline
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

## 3. Schema Auth

ID ngẫu nhiên kiểu Guid, timestamp `DateTimeOffset` UTC, lấy giờ qua TimeProvider.
P03 đã hiện thực các bảng dưới đây. Mapping nằm ở `Data/Configurations/`, migration ở `Data/Migrations/`;
SQL review nằm ở [001-InitialAuth.sql](Sql/001-InitialAuth.sql).

| Entity | Cột chính | Constraint/index quan trọng |
|---|---|---|
| User | Id, Email, NormalizedEmail, DisplayName, PasswordHash nullable, EmailConfirmedAt, Status, MfaEmailEnabled, AuthVersion, FailedLoginCount, FailedLoginWindowStart nullable, LockoutEnd, CreatedAt, UpdatedAt, RowVersion | Unique NormalizedEmail, rowversion |
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

P03 dùng email trim + `ToUpperInvariant`; `NormalizedEmail` có binary collation và unique index.
DbContext chuẩn hóa trước save; digest token/challenge/recovery là hex viết hoa 64 ký tự,
SQL constraint chặn digest không đúng format. Provider subject dùng binary collation, phân biệt casing.
`AuthUnitOfWork` dùng transaction SQL mặc định ReadCommitted; P07/P11 phải bổ sung khóa/isolation
theo flow, không coi thao tác đọc tracked entity là khóa row. Chưa bật automatic retry/replay transaction.
Seed role có GUID cố định và không tạo tài khoản Admin. Audit/history chặn update/delete qua
SaveChanges của DbContext; raw SQL/ExecuteUpdate hoặc quyền DB vẫn có thể sửa nên không phải tamper-proof.

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

P03 xác minh format IdentityV3: PBKDF2-HMAC-SHA512, salt 128 bit, subkey 256 bit,
210.000 iterations mặc định; cấu hình cho phép 210.000–1.000.000, luôn IdentityV3.
Hash malformed trả Failed; hash cũ trả NeedsRehash, hash mạnh hơn không bị yêu cầu hạ work factor.
Rehash/lưu hash khi login thuộc P06. Đo Release cục bộ 20 mẫu: hash p50 85,0 ms, p95 88,6 ms;
verify p50 86,2 ms, p95 264,2 ms. Đây là microbenchmark máy hiện tại, chưa phải load test/quota P13.
[Mã nguồn PasswordHasher](https://source.dot.net/Microsoft.Extensions.Identity.Core/PasswordHasher.cs.html).

## 5. JWT, session và revocation

P08 Forgot/Reset dùng challenge ResetPassword và protected PasswordEmail outbox. Reset/change khóa
User trước session/family mutations, hash + tăng AuthVersion + consume outstanding challenges + revoke
all sessions/refresh + audit/notification cùng transaction. MFA/recovery codes không bị tắt hoặc xóa.
Google-only account không nhận reset challenge và không tự có password. Link fixed HTTPS + fragment,
không token query/GET. SMTP worker tách khỏi verification worker P05, kind/purpose Data Protection riêng.

Change/reauthenticate lấy actor/session/av từ validated JWT; current password qua credential gate P04,
SQL lockout giữ nguyên. Completion kiểm tra version/session/user dưới locks. Recent primary/factor
dựa timestamp SQL, < 5 phút mặc định, reject future proof. Refresh không renew AuthenticatedAt hoặc MFA.
Password reauthenticate chỉ renew AuthenticatedAt, không MFA/idle/absolute/version, không cấp token;
factor proof missing/expired cần P09, Google reauthenticate cần P10. Audit Reauthenticated có thể phục vụ
recent auth policy về sau; không dùng chỉ iat của JWT làm bằng chứng fresh authentication.

Access JWT baseline 5 phút, clock skew tối đa 30 giây; claims tối thiểu `sub`, `jti`, `sid`,
`iss`, `aud`, `iat`, `nbf`, `exp`, `av` (AuthVersion) và `amr` (P06: pwd).
P06 không đưa role vào JWT; role claims được gắn từ SQL sau session validation trên mỗi request.
Không đưa PII nhạy cảm/password/OTP vào claims. Khóa ký asymmetric RS256, `kid` và allowlist algorithm;
reject alg=none/wrong key/issuer/audience. Secrets và private key ở env/secret store, không source.
Key rotation giữ verification key cũ đến khi token đã phát hết hạn + skew.
P06 mới có một active RSA key, typ at+jwt, kid bắt buộc trùng key ID; multi-key rotation thuộc P13.
Startup kiểm tra PEM private RSA ≥ 2048 bit; CLI tạo key local 3072 bit bằng CreateNew, không ghi đè/in key.
File PEM/key không được copy ra build/publish; deployment phải cấp key path ngoài package và ACL phù hợp.

Sau kiểm tra JWT cryptographic, lấy session/user từ SQL cho **mỗi request protected**:
session active + user active + auth version khớp. Role mutation tăng version/revoke để JWT role cũ vô hiệu.
DB validation unavailable → fail closed (503 chuẩn), không bỏ qua revocation. MVP không cache kết quả active;
nếu thêm cache sau này phải định nghĩa độ trễ revoke/invalidation đa instance.
Logout có hiệu lực cho request được xác thực sau transaction commit; request đã chạy trước commit
không thể bị thu hồi ngược. Thao tác cực nhạy cảm kiểm tra lại trạng thái trong transaction.

P06 dùng JwtBearer 10.0.12, MapInboundClaims=false, valid algorithms chỉ RS256, valid type at+jwt,
issuer/audience exact (không bỏ qua trailing slash), lifetime dùng TimeProvider để test boundary.
sub/sid/jti phải Guid khác rỗng; av ≥ 1, iat hợp lệ/không tương lai; claims cần thiết không được trùng.
Authorization header giới hạn 8192 ký tự; không nhận JWT query/cookie. Callback 401/403 qua ProblemDetails,
không trả raw token/security exception. Key không nạp khi module tắt.

Password gate commit primary-factor audit/counter ở transaction P04. LoginStore tạo transaction mới,
khóa lại User và kiểm tra trạng thái/version/lockout trước rehash + session/history/audit commit.
Signing/audit/save failure rollback transaction completion; primary-factor audit đã commit vẫn giữ lại.
Account MFA chỉ có history Pending và challenge trước factor; module MFA tắt vẫn 403 step-up. Chỉ verify thành công mới cấp full session.
Idle/absolute session expiry được tính tại login, mặc định 7/30 ngày; P06 chưa extend idle/LastSeenAt mỗi request.
P07 thay digest dự phòng bằng secret CSPRNG cấp qua cookie khi module Refresh bật.
Không tăng LastSeenAt/idle trên GET hoặc bearer request; chỉ rotation thành công kéo dài idle.

Refresh token: ít nhất 32 random bytes CSPRNG → Base64URL; DB lưu SHA-256 digest, không plaintext.
Hash nhanh phù hợp token entropy cao; không dùng thiết kế này cho OTP/password entropy thấp.
Baseline idle expiry 7 ngày, absolute lifetime session 30 ngày; refresh không kéo dài absolute expiry.

Rotation transaction:

1. Lookup token hash → user/session ID; lock User trước AuthSession bằng UPDLOCK/ROWLOCK trong transaction,
   đọc lại token rồi check account/version/expiry/revocation. Mutator P07 và login cùng khóa User trước.
2. Nếu token active: consume dưới session lock, SaveChanges dùng rowversion; không thể rotate cùng lúc.
3. Insert successor, đặt ReplacedBy, cập nhật session idle expiry, audit; commit rồi mới trả token.
4. Nếu token đã consumed: revoke session + toàn family, lưu audit **và commit**; sau đó trả 401.
   Không throw trước commit khiến transaction rollback mất dấu revoke.
5. Token ngẫu nhiên không tồn tại chỉ trả generic invalid; không suy đoán family của user.

Mặc định strict reuse: hai refresh song song có tối đa một response success, request còn lại
revoke family; token mới của request thắng cũng không còn dùng được sau revoke commit.
Client phải single-flight refresh. Retry sau network timeout có thể yêu cầu login lại;
không mở grace window khi chưa có thiết kế idempotency chống replay.
Refresh-vs-logout cùng khóa session để không sinh successor active sau revoke.
P07 dùng ba SaveChanges trong cùng transaction: consume để giải phóng unique index active-token,
insert successor/audit rồi gán ReplacedById sau khi FK target tồn tại; không có trạng thái nửa chừng được commit.
Signing/protection/save failure rollback. Logout-all tăng AuthVersion dưới user lock, revoke các family của actor;
primary-factor acceptance trước logout-all bị từ chối khi login completion kiểm tra lại version.
Consumed tombstone còn được kiểm tra khi expiry cũ đã qua nhưng family vẫn active. Reuse được xử lý
trước quota session; session đã revoke/expired hoặc account không còn hợp lệ thì trả invalid, không audit lặp.
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

P07 dùng ASP.NET Core IAntiforgery + Data Protection, cookie context HttpOnly/Secure/Strict/Path=/,
request token trong header X-CSRF-TOKEN. Token context độc lập với identity access bằng cách dùng
anonymous principal chỉ trong GetAndStoreTokens/ValidateRequestAsync, khôi phục actor trong finally;
ownership/authorization vẫn dùng validated bearer actor. Điều này giữ CSRF hợp lệ qua login/access expiry.
Login cũng kiểm tra CSRF vì cấp cookie; logout-all và owner revoke cùng kiểm tra header/origin.
Origin exact configured HTTPS authority, hoặc Referer origin nếu Origin absent; không có cả hai bị từ chối.
CSRF/Origin không chứng minh user identity, không bảo vệ khỏi XSS, không thay thế JWT hoặc cookie ownership.
Module mặc định tắt, bật yêu cầu JWT/Persistence và allowlist hợp lệ. Swagger hiển thị X-CSRF-TOKEN
trên các thao tác có filter; lấy token/cookie bằng GET csrf trong cùng browser trước Try it out.

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
  resend cách 60 giây và tối đa 3 lần gửi/challenge, tổng 3 lần/user trong window 600s kể cả challenge consumed;
  OTP IP quota 10/phút, step-up password dùng Login quota/SQL lockout.
- Resend thay code/version, giữ total failed attempts và absolute challenge deadline.
- Bind challenge vào user/purpose/pre-auth context; không nhận userId/email để đổi đối tượng khi verify.
- OTP consume + tạo session + audit atomic; response mới có token khi factor hoàn tất.
- Pre-auth cookie Data Protection purpose riêng, bind challenge/user/purpose/random ContextId/AuthVersion;
  không được JwtBearer nhận làm access token. Một cookie chứa một operation, flow mới ghi đè binding browser.
- MFA enrollment cần recent primary auth ≤ 5 phút và OTP; disable cần current factor/recovery code.
- Recovery codes entropy cao, hiển thị một lần, lưu hash, atomic one-time; regenerate vô hiệu bộ cũ.

P09 enable/disable/regenerate tăng AuthVersion và revoke mọi session/refresh/challenge cùng transaction.
Step-up endpoint riêng xác minh password rồi factor, completion mới cập nhật AuthenticatedAt/MfaVerifiedAt;
resend không renew primary proof. JWT amr có pwd + mfa khi session đã có factor; refresh giữ bằng chứng SQL.
Pepper hiện chỉ có một key ID active, đổi key sẽ invalid pending OTP. P10 đã nối Google primary/factor completion.

## 8. Google OAuth 2.0 / OpenID Connect

OAuth cấp quyền, OIDC xác minh danh tính. Dùng Authorization Code + PKCE S256, state, nonce,
exact redirect URI, backchannel TLS và metadata/JWKS được middleware kiểm tra.
Identity bền vững là Google `sub`, không email. Validate email_verified khi dùng để xác minh địa chỉ.
Không nhận email/avatar/role client gửi như bằng chứng đăng nhập.

Callback tìm ExternalLogin; nếu chưa có thì tạo User hoặc yêu cầu link explicit nếu email đã tồn tại.
Link bind current authenticated user + recent auth + OAuth state có purpose Link; không ghi đè identity
đã thuộc user khác. Login/link callback không phát token qua redirect query/fragment.
Completion ticket một lần ≤ 60 giây bind browser correlation; redeem bằng POST kiểm tra CSRF/origin,
rồi cấp token hoặc challenge local MFA. URL callback/completion/error cấu hình cố định,
HTTPS, không query/fragment/userinfo; origin client phải trong Refresh.AllowedOrigins.
Không sử dụng returnUrl từ caller hoặc Host header để dựng redirect.
[Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect).

P10 dùng Microsoft OpenIdConnect middleware, code + PKCE, scopes chỉ openid/email/profile,
RS256 issuer/audience/lifetime validation, nonce/correlation; SaveTokens=false và chặn external
cookie sign-in để provider principal không trở thành phiên API. JWT chỉ phát khi completion
hoặc local MFA thành công; amr là federated, thêm mfa khi có factor SQL hợp lệ.
AuthSession/AuthChallenge.PrimaryMethod có default Password và CHECK Password/Google;
migration AddPrimaryAuthenticationMethod backfill session/challenge cũ, giữ HMAC OTP Password tương thích P09.
Google OTP HMAC thêm method vào input để ràng buộc primary method.

GoogleFlow cookie DP HttpOnly/Secure/Lax path / dùng cho callback GET; GoogleCompletion
DP HttpOnly/Secure/Strict path /api/v1/auth cho POST sau redirect. Ticket 32 random bytes,
chỉ SHA256 digest trong SQL; challenge bind context/user/purpose/AuthVersion và consumed atomic.
Một browser một flow; khởi tạo flow khác thay cookie binding cũ. Callback/account creation
không cấp phiên; pending history tách success sau completion/factor. User lock serialize các mutation.
Link/unlink yêu cầu recent primary và local MFA proof khi bật MFA; thay identity revoke toàn bộ phiên.
Không xóa login method cuối cùng và không provision password cho Google-only qua recovery.

POST link/reauthenticate trả authorizationUrl JSON sau middleware đã tạo protected state/PKCE;
frontend điều hướng browser, không gửi JWT cho Google. Reauthentication yêu cầu max_age=0,
signed auth_time còn recent và phù hợp thời điểm bắt đầu flow; callback về chậm không làm mới
primary proof cũ. Nếu bật MFA, completion chỉ stage local StepUp; proof chỉ hoàn tất sau OTP/recovery.
Provider thiếu auth_time trong reauthentication bị từ chối, không fallback sang callback timestamp.

Log category Microsoft.AspNetCore.Authentication.OpenIdConnect bị tắt vì middleware có thể
ghi raw error_description do provider trả. AuthProject.GoogleProtocol chỉ ghi public error code
và server traceId, không exception/raw provider body. Audit nghiệp vụ vẫn ghi SQL/protected outbox;
delivery log4net và retention thuộc P12. Test P10 đã tái hiện và chặn raw protocol error trong log.

## 9. Rate limiting và lockout

Baseline configurable: login 10 request/phút/IP và 5 password failure/account/15 phút → khóa 15 phút;
register/forgot/resend 5 request/15 phút/IP, email send 3/15 phút/account;
OTP 5 attempt/challenge + 10 verify/phút/IP; refresh 30/phút/session và 60/phút/IP;
Google start/completion 10/phút/IP. Các limiter xếp lớp, không dùng quota IP thay account counter.

P04 dùng middleware ASP.NET Core với store fixed-window tự giới hạn partition; không đăng ký
`UseRateLimiter`. Quota hiện tại: API 200/phút/IP; login 10/phút/IP + 30/15 phút/account;
register và recovery 5/15 phút/IP + 5/15 phút/account; OTP 10/phút/IP; refresh 60/phút/IP
+ 30/phút/session đã xác thực; Google 10/phút/IP. Quota email send/challenge ở trên thuộc P05/P09.
Global quota áp dụng trước khi đọc body; policy quota lấy từ `[AuthRateLimit(...)]` trên endpoint.
Account partition dùng HMAC-SHA256 của normalized email với key ngẫu nhiên mỗi process, không giữ email
thô trong dictionary. Tối đa 10.000 partitions mặc định, key ≤ 128 ký tự; hết capacity trả 429,
không evict counter còn hiệu lực. Fixed window tính từ request đầu tiên, có thể burst tại ranh giới.
Restart process reset quota memory; lockout SQL giữ nguyên.

Limiter memory bảo vệ process; lockout SQL shared. Nhiều instance cần distributed limiter/edge
shared quotas ở P13, không tuyên bố limiter in-memory bảo vệ toàn cluster. Giới hạn memory partitions,
header/body/key length để attacker không tạo state vô hạn. Login response giống nhau cho account không
tồn tại/sai password/lockout; logs nội bộ phân biệt reason nhưng không trả cho client.
Kiểm tra timing bằng benchmark, không hứa timing tuyệt đối giống nhau trên mạng.
[OWASP Authentication](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html).

Lockout P04 lưu `FailedLoginWindowStart`, tối đa 5 lần sai/900 giây, khóa 900 giây.
`PasswordAttemptStore` dùng DbContext riêng qua scoped factory; đọc lại user với SQL
`UPDLOCK, ROWLOCK` trong transaction trước cập nhật, so lại password hash/AuthVersion với snapshot
đã verify. Counter + audit/outbox cùng commit; lỗi audit rollback toàn bộ. Request khi đang khóa
không tăng count hoặc kéo dài khóa. Hết khóa cho thử lại, password đúng reset count/window.
`PasswordCredentialGate` validate input, luôn verify hash; unknown/Google-only dùng dummy hash cùng
work factor. Unknown/wrong/locked/disabled/unverified đều trả `AUTH_INVALID_CREDENTIALS` 401.
Thành công mới là primary factor (`PasswordVerified`, outcome Pending); P06/P09 hoàn tất phiên/MFA.
Session quota chỉ lấy `sid` từ principal server đã xác thực; P06 cần đặt authentication trước
middleware policy. P07 enforce quota session của anonymous refresh sau khi tra token đã xác minh,
không dùng token hoặc session ID chưa tin cậy làm key.

Trusted proxy mặc định tắt; loại bỏ implicit loopback trust. Khi bật phải khai báo
`KnownProxies`/`KnownNetworks`, không chấp nhận `/0`, `ForwardLimit` 1–5. Chỉ xử lý
`X-Forwarded-For` + `X-Forwarded-Proto` với số phần tử khớp nhau; peer không được tin cậy bị bỏ qua.
Middleware chạy trước quota IP và HTTPS redirect; proxy phải xử lý header từ client đúng cách.
Tham khảo [Microsoft forwarded headers](https://learn.microsoft.com/en-gb/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## 10. Audit và vận hành

P05 thêm email outbox riêng: payload Data Protection purpose `AuthProject.VerificationEmail.v1`,
challenge chỉ lưu SHA-256 digest của 32 random bytes; raw token chỉ trong protected payload/email.
User pending + default User role + challenge + audit/outbox cùng commit. Register khóa range email
bằng `UPDLOCK,HOLDLOCK`; Resend/Verify khóa User trước challenge để serialize state transitions.
Resend invalidate các token verify chưa consume, không reset send budget 3 email/900 giây.
Verify một lần, ràng buộc purpose/expiry/AuthVersion; chưa tạo session hoặc login success.

SMTP adapter MailKit yêu cầu TLS (465 SSL-on-connect, port khác mandatory STARTTLS), giữ certificate
validation mặc định. Link base là HTTPS URL cấu hình cố định, không lấy Host header;
token nằm fragment để client POST xác minh, không nằm HTTP query. P05 chưa tạo trang frontend.
Email worker claim bằng conditional rowversion trong transaction ngắn; lưu lease 120 giây,
send timeout 30 giây, retry exponential 30–900 giây, tối đa 10 attempts. Trước send, khóa User và
kiểm tra challenge/current lease; giữ user lock trong SMTP send để serialize với resend/verify.
Đây là tradeoff giữ SQL connection/lock khi gửi, cần đo ở P13. Không xử lý outbox Audit ở worker này.
Delivery at-least-once: crash sau SMTP accept trước SQL acknowledgment có thể gửi lặp; stable Message-ID
không bảo đảm provider deduplicate. Event quá 10 attempts vẫn pending để điều tra/requeue có kiểm soát;
monitoring/cleanup/delivery audit worker hoàn thiện ở P12.

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

P03 có `IAuditWriter.Stage` tạo audit và outbox cùng DbContext, chưa tự save/commit hoặc gọi file sink.
Metadata chỉ nhận session ID, authentication method và count; không nhận dictionary/text tự do.
TraceId phải lấy từ server, không dùng request body/header credentials. Outbox audit được mã hóa
bằng Data Protection purpose `AuthProject.AuditOutbox.v1`. Log4net adapter xuất JSON vào RollingFileAppender
(10 MB/file, 10 backup), mặc định tắt; chưa có worker, retry, retention theo ngày hoặc delivery acknowledgment.
Key directory có cấu hình/ignore; ACL, mã hóa key lúc lưu và shared key store production là gate P13.

Baseline retention dự kiến: login history 90 ngày, audit 365 ngày, local file 30 ngày theo size/time;
chốt theo yêu cầu vận hành trước production. Quyền đọc/sửa file và audit DB được giới hạn;
không tuyên bố append-only application tương đương tamper-proof storage.

## 11. Những phần chưa nằm trong baseline

UI/frontend; multi-tenant; permission catalog động; OAuth server cho bên thứ ba; TOTP/passkey;
SMS OTP; SSO doanh nghiệp; impersonation; account deletion/export; change email;
dịch vụ hỗ trợ thủ công lấy lại account khi mất toàn bộ factor. Thiết kế extension points đủ nhỏ,
chưa tạo abstractions chỉ để dự phòng các feature này.
