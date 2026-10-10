# API, DTO và lỗi thống nhất

**P08 đã triển khai các flow P05–P07, Forgot/Reset/Change password và password reauthenticate; các endpoint còn lại chờ bước tương ứng.** Prefix `/api/v1`.
JSON camelCase, UTC ISO-8601, ID opaque/Guid. Thành công dùng DTO trực tiếp, không bọc mọi
response trong `success/message/data`; lỗi dùng `application/problem+json` thống nhất.

## 1. Endpoint catalog

`Anonymous` vẫn chịu rate limit/validation/CSRF nếu liên quan browser cookie.
`Step-up` là bằng chứng recent primary auth và factor khi bật MFA, không chỉ access JWT bất kỳ.

| Method + path | Quyền | Input / kết quả chính | Bước |
|---|---|---|---|
| GET /auth/csrf | Anonymous, allowed browser origin | Anti-forgery request token + context cookie, no-store | P07 |
| POST /auth/register | Anonymous | RegisterRequest → generic 202, không tokens | P05 |
| POST /auth/email/verify | Anonymous | VerifyEmailRequest → 204 | P05 |
| POST /auth/email/resend | Anonymous | EmailRequest → generic 202 | P05 |
| POST /auth/login | Anonymous | LoginRequest → 200 AuthResult hoặc 202 MfaChallengeResult | P06/P09 |
| POST /auth/refresh | Refresh cookie + CSRF | 200 AuthResult, rotate cookie | P07 |
| POST /auth/logout | Refresh cookie + CSRF, access có thể hết hạn | 204, revoke family và clear cookie; cookie absent/invalid vẫn 204 | P07 |
| POST /auth/logout-all | Authenticated + CSRF | 204, revoke tất cả session của actor | P07 |
| GET /auth/me | Authenticated | 200 MeResponse | P06 |
| GET /auth/sessions | Authenticated | Danh sách session của actor, phân trang | P07 |
| DELETE /auth/sessions/{sessionId} | Owner + CSRF | 204; unknown/non-owner cùng 404 | P07 |
| POST /auth/password/forgot | Anonymous | EmailRequest → generic 202 | P08 |
| POST /auth/password/reset | Anonymous | ResetPasswordRequest → 204, không auto-login | P08 |
| POST /auth/password/change | Authenticated + recent primary/factor + CSRF khi Refresh bật | ChangePasswordRequest → 204, revoke tất cả session | P08/P09 |
| POST /auth/reauthenticate | Authenticated + CSRF khi Refresh bật | ReauthenticateRequest.password → 204, renew primary proof; Google/factor challenge về sau | P08–P10 |
| POST /auth/mfa/email/enroll | Authenticated + recent primary auth | 202 enrollment challenge | P09 |
| POST /auth/mfa/email/enable | Authenticated + enrollment challenge | VerifyOtpRequest → recovery codes một lần; revoke phiên cũ | P09 |
| POST /auth/mfa/email/verify | Pre-auth challenge context | VerifyOtpRequest → AuthResult hoặc step-up ticket theo purpose | P09 |
| POST /auth/mfa/email/resend | Pre-auth/enrollment context | ChallengeRequest → generic 202, giữ budget | P09 |
| POST /auth/mfa/email/disable | Authenticated + step-up/current factor | 204; revoke sessions + invalidate codes/challenges | P09 |
| POST /auth/mfa/recovery/verify | Pre-auth challenge context | RecoveryCodeRequest → AuthResult, consume code | P09 |
| POST /auth/mfa/recovery/regenerate | Authenticated + step-up/current factor | Bộ codes mới một lần, bộ cũ hết hiệu lực | P09 |
| GET /auth/google/start | Anonymous, browser navigation | 302 Google, bind login state + return path | P10 |
| GET /signin-google | Provider callback, correlation/state | Middleware callback → redirect fixed completion page | P10 |
| POST /auth/google/complete | Completion ticket + browser binding/CSRF | AuthResult hoặc MfaChallengeResult | P10 |
| POST /auth/google/link | Authenticated + step-up/CSRF | Bắt đầu provider challenge gắn actor + Link purpose | P10 |
| DELETE /auth/google/link | Authenticated + step-up | 204; không được xóa login method cuối | P10 |
| GET /users/me/login-history | Authenticated | History chỉ actor, phân trang | P12 |
| GET /admin/users | Admin + MFA session | Danh sách user, phân trang/filter | P11 |
| PATCH /admin/users/{userId}/status | Admin + step-up | ChangeUserStatusRequest → 204 + revoke | P11 |
| GET /admin/roles | Admin + MFA session | Role catalog | P11 |
| PUT /admin/users/{userId}/roles | Admin + step-up | SetUserRolesRequest → 204 + revoke | P11 |
| GET /admin/audit-logs | Admin + MFA session | Audit filters + pagination | P12 |

Mặc định role catalog chỉ seed User/Admin; custom role CRUD/permission editor chưa cần khi chưa có business feature.
Callback `/signin-google` là protocol route middleware, không áp schema JSON cho 302 thành công;
lỗi protocol redirect về fixed error page chỉ có mã lỗi công khai + traceId, không raw provider response.
Logout không chấp nhận `sessionId/userId` từ body để revoke tài khoản khác; chỉ dùng digest của cookie.
Session delete và logout-all require access JWT; logout hiện tại vẫn làm được khi access hết hạn.

## 2. DTO validation

| DTO / field | Quy tắc |
|---|---|
| RegisterRequest.email / EmailRequest.email | Required, email format, tối đa 254 ký tự, trim/normalize trong server |
| RegisterRequest.password | Required, 15–128 ký tự; password policy; giữ nguyên nội dung |
| RegisterRequest.displayName | Required, sau trim 1–100 ký tự; không HTML rendering trực tiếp |
| LoginRequest.email | Required, max 254, normalized lookup |
| LoginRequest.password | Required, max 128; không áp minimum của password mới để chặn password legacy |
| VerifyEmailRequest.token / ResetPasswordRequest.token | Required, Base64URL bounded length (tối đa 512), purpose/expiry kiểm tra trong use case |
| ResetPasswordRequest.newPassword | Chính sách password mới |
| ChangePasswordRequest.currentPassword/newPassword | Required, bounded; mới theo policy; không được same password |
| ReauthenticateRequest.password | Required, tối đa 128 ký tự, không trim; không nhận actor/session/role |
| VerifyOtpRequest.challengeId | Guid hợp lệ, bind với pre-auth context, không nhận userId thay thế |
| VerifyOtpRequest.code | Chính xác 6 ASCII digits, string, không trim tự động |
| ChallengeRequest.challengeId | Guid hợp lệ và ownership/context verification |
| RecoveryCodeRequest.challengeId/code | Guid khác rỗng; code 43 ký tự Base64URL (định hướng 32 random bytes); generator/digest lookup ở P09 |
| GoogleCompleteRequest.ticket | Opaque bounded, one-use, expiry, browser correlation |
| SetUserRolesRequest.roleNames | Required, không trùng, tối đa 10; role phải thuộc catalog |
| ChangeUserStatusRequest.status | Allowlist Active/Disabled; không sửa trực tiếp trạng thái verification |
| Paging.page/pageSize | page ≥ 1, pageSize 1–100, mặc định 20 |
| HistoryQuery.from/to/sort | Cả hai thời gian hoặc không có; UTC, from ≤ to, tối đa 90 ngày; sort occurredAtAsc/occurredAtDesc |

Không cho client bind entity trực tiếp. DTO Register không có Role, EmailConfirmed, Status,
PasswordHash, AuthVersion. Unknown sensitive property được reject bằng DTO binding policy,
không im lặng chấp nhận trong security DTO. Validation shape tại API; invariant nghiệp vụ
(uniqueness, ownership, active user, replay) vẫn kiểm tra ở Services + DB constraints.
Messages dễ hiểu; code ổn định, không để client phụ thuộc câu tiếng Việt/Anh cụ thể.

P02 kiểm tra hình dạng input; chưa truy vấn DB, kiểm tra token còn hạn/ownership/replay,
hoặc tra password bị lộ. Validators không sửa dữ liệu: service P05 sẽ normalize email,
trim displayName trước lưu; password luôn giữ nguyên. Role catalog P02 là User/Admin.
Unknown property bất kỳ trong security JSON DTO bị reject, không chỉ các field nhạy cảm.

P05: ba endpoint Register/Verify/Resend giữ hợp đồng ở trên, `Cache-Control: no-store`.
Khi Registration chưa bật, input hợp lệ trả `503 SERVICE_UNAVAILABLE`; input sai vẫn được filter P02 xử lý.
Verify token sai/expired/replay/wrong purpose/security version hoặc account không pending trả
`400 AUTH_CHALLENGE_INVALID`. Verify dùng POST JSON, không có GET tự consume token.
Resend áp dụng cooldown 60 giây, tối đa 3 email/900 giây/account (tính cả Register); bị budget chặn
vẫn generic 202. IP/account limiter P04 có thể trả 429 độc lập với send budget SQL.
Không trả userId, challengeId, token hay mailbox ở Register/Resend; chưa có JWT/session.

## 3. Response thành công mẫu

AuthResult (200):

```json
{
  "accessToken": "<jwt>",
  "tokenType": "Bearer",
  "expiresIn": 300,
  "user": { "id": "<guid>", "email": "user@example.com", "displayName": "User", "roles": ["User"] }
}
```

Refresh token chỉ trong Set-Cookie HttpOnly theo browser baseline; không trong JSON.
P07 cấp refresh cookie khi `Auth:Refresh:Enabled=true`; Login yêu cầu CSRF trong chế độ này.
Khi module Refresh tắt, Login vẫn dùng chế độ access-only P06 và không gửi refresh cookie.
Account đã bật MFA nhận 403 AUTH_STEP_UP_REQUIRED sau password hợp lệ, chưa có challenge/tokens đến P09.
Token response có `Cache-Control: no-store`. Register/forgot/resend cùng generic 202
`{"message":"Nếu yêu cầu hợp lệ, hướng dẫn sẽ được gửi đến email của bạn."}`.

MfaChallengeResult (202):

```json
{
  "requiresMfa": true,
  "challengeId": "<guid>",
  "method": "emailOtp",
  "expiresIn": 300,
  "resendAfter": 60
}
```

Pre-auth context được server ràng buộc bằng cookie/ticket riêng; không JWT/refresh ở response này.
Không trả mailbox mask trước khi primary factor đã hợp lệ.
MeResponse gồm id/email/displayName/roles/emailVerified/mfaEnabled/createdAt;
không trả hash, security version, provider secret, challenge digest hay failed-login counter.
Paged response: `{ "items": [], "page": 1, "pageSize": 20, "totalCount": 0 }`.

P06: GetMe nhận actor từ validated `sub`, không dùng userId query để chọn user khác. Token chỉ nhận
từ Authorization: Bearer; không query/cookie. Mọi controller endpoint mặc định require auth qua
MapControllers.RequireAuthorization(), anonymous explicit bằng AllowAnonymous. Route không tồn tại vẫn 404.
JWT/session không hợp lệ trả 401 AUTH_UNAUTHENTICATED + WWW-Authenticate: Bearer; thiếu role trả
403 AUTH_FORBIDDEN. Session lookup gặp lỗi DB trả safe 503, không bỏ qua kiểm tra revoke.
Login credential sai/unknown/locked/disabled/unverified/Google-only vẫn chung AUTH_INVALID_CREDENTIALS 401.
Module JWT tắt: Login hợp lệ trả 503, GetMe không token trả 401. Swagger có Bearer; anonymous operations không yêu cầu token.

## 4. Hợp đồng lỗi

Ví dụ 400:

```json
{
  "type": "urn:authproject:problem:validation-failed",
  "title": "Dữ liệu không hợp lệ.",
  "status": 400,
  "instance": "/api/v1/auth/register",
  "code": "VALIDATION_FAILED",
  "traceId": "<correlation-id>",
  "errors": {
    "email": [{ "code": "EMAIL_INVALID", "message": "Email không hợp lệ." }]
  }
}
```

`type/title/status/instance/code/traceId` bắt buộc; `errors` chỉ khi có field errors;
`detail` tùy chọn nhưng phải là thông tin công khai. `instance` chỉ path, không query chứa token.
Không stackTrace/innerException/SQL/provider response/secret kể cả API chạy Development.
Log nội bộ có exception đã redaction và traceId để điều tra.

P02 chỉ ghi exception type + errorCode + traceId, không ghi raw exception/message/stack/Data.
Body binding lỗi dùng `INVALID_REQUEST` với field `request` và message chung để tránh echo input.
FluentValidation lỗi dùng `VALIDATION_FAILED` và field name camelCase. Response lỗi có
`Cache-Control: no-store` và `X-Trace-Id` khớp `traceId` trong JSON, kể cả client Accept text/html.

| HTTP | Code mẫu | Ý nghĩa |
|---|---|---|
| 400 | VALIDATION_FAILED / INVALID_REQUEST | DTO, malformed JSON, query binding |
| 400 | AUTH_CHALLENGE_INVALID | OTP/reset/verify sai, hết hạn, dùng lại (generic theo flow) |
| 401 | AUTH_INVALID_CREDENTIALS | Unknown user, sai password, lockout; cùng body/status |
| 401 | AUTH_UNAUTHENTICATED / AUTH_SESSION_INVALID | Không token, JWT không hợp lệ, session revoke |
| 401 | AUTH_REFRESH_INVALID | Refresh sai/expired/reuse; reason chi tiết chỉ ở audit |
| 403 | AUTH_FORBIDDEN / AUTH_STEP_UP_REQUIRED | Đã xác thực nhưng không có quyền/bằng chứng gần đây |
| 403 | AUTH_CSRF_INVALID | Origin/Referer hoặc CSRF cookie/header không hợp lệ |
| 404 | RESOURCE_NOT_FOUND | Missing route/resource hoặc hide non-owner |
| 405 | METHOD_NOT_ALLOWED | Giữ Allow header khi route có method khác |
| 409 | RESOURCE_CONFLICT / AUTH_LAST_ADMIN / AUTH_LAST_LOGIN_METHOD | Xung đột hợp lệ để công khai |
| 413 | REQUEST_TOO_LARGE | Body vượt giới hạn ứng dụng |
| 415 | UNSUPPORTED_MEDIA_TYPE | Content-Type không được hỗ trợ |
| 429 | RATE_LIMITED | Có Retry-After; không trả internal account counter |
| 500 | INTERNAL_ERROR | Lỗi bất ngờ, message generic |
| 503 | SERVICE_UNAVAILABLE | Dependency cần thiết không sẵn sàng, fail closed |

Không dùng 409 duplicate email ở anonymous register nếu chọn chống enumeration (trả generic 202).
Chưa verified/disabled không phát session; public login có thể cùng AUTH_INVALID_CREDENTIALS,
UX hướng dẫn xác minh qua endpoint resend generic.

Services trả typed result/error cho thất bại dự kiến; chỉ dùng exception có loại rõ ràng
khi cần unwind. API có mapper tập trung từ code → HTTP/type/title. `IExceptionHandler`
xử lý lỗi bất ngờ/domain exception qua cùng factory. Validator/filter, model binding,
JwtBearer Challenge/Forbidden, rate limiter OnRejected, StatusCodePages cũng dùng factory này.
Middleware không thể bọc response bị reverse proxy/Kestrel từ chối trước pipeline hoặc kết nối đã ngắt;
P13 ghi rõ phạm vi và cấu hình edge error response khi cần.

P02 đã nối filter/model binding/exception/StatusCodePages/IProblemDetailsService vào cùng writer.
Filter result đổi error payload tùy ý của controller thành schema chuẩn; dùng `ApiProblemResult`
khi cần giữ code cụ thể trong catalog. Callback JwtBearer và OnRejected của rate limiter sẽ nối ở
P06/P04; test P02 kiểm chứng response 401/403/429 và bảo toàn protocol headers, chưa kiểm chứng JWT/quota.

## 5. Hợp đồng session P07

- `GET /auth/csrf`: `{ "requestToken": "<antiforgery-token>" }`, kèm cookie
  `__Host-AuthProject.Csrf`, HttpOnly/Secure/SameSite=Strict/Path=/, không Domain.
- Login/Refresh/Logout/Logout-all/DELETE session cần header `X-CSRF-TOKEN` và context cookie
  khi Refresh bật. Origin phải exact allowlist; nếu không có Origin, dùng origin của Referer.
  Thiếu cả hai hoặc `Origin: null` → 403. CSRF request token bind context cookie, độc lập với
  bearer identity để tiếp tục dùng sau login hoặc access expiry; bearer principal được giữ nguyên
  cho authorization/ownership. CSRF token không thay thế authentication.
- Refresh nhận cookie `__Secure-AuthProject.Refresh`, không nhận token từ JSON/query.
  Secret 32 bytes CSPRNG, canonical Base64URL 43 ký tự; DB chỉ lưu SHA-256 digest.
  Cookie HttpOnly/Secure/SameSite=Strict/Path=/api/v1/auth, không Domain, Expires theo idle/absolute cap.
- Refresh success → AuthResult và cookie mới; không tạo LoginHistory success mới. `expiresIn`
  tối đa cấu hình JWT và được giới hạn bởi thời gian session còn lại. Refresh kéo dài idle,
  cập nhật LastSeenAt, giữ nguyên absolute expiry/AuthenticatedAt/AuthVersion.
- Refresh invalid/unknown/expired/consumed → generic 401 AUTH_REFRESH_INVALID và clear refresh
  cookie đúng flags/path. Consumed token của family còn hoạt động revoke toàn family, commit trước 401.
  Strict reuse: parallel refresh có tối đa một success; successor cũng mất hiệu lực sau reuse commit.
  Client phải single-flight; mất response sau commit có thể phải login lại, chưa có grace window.
- Quota refresh: IP ở middleware; session sau lookup token/session tin cậy. 429 RATE_LIMITED +
  Retry-After, không consume hoặc clear cookie. Reuse của consumed token được xử lý trước quota session.
- Logout dùng cookie để chọn family, không cần access JWT còn hạn. Missing/invalid/already revoked
  cookie vẫn 204 + clear cookie nếu CSRF hợp lệ. Logout-all cần JWT + CSRF, tăng AuthVersion và revoke
  toàn session của actor, chặn primary-factor acceptance cũ hoàn tất login sau đó.
- Sessions nhận PagingRequest, page ≥ 1, pageSize 1–100, order CreatedAt DESC rồi Id; actor từ sub.
  Response `{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 0 }`.
  Mỗi item có id/createdAt/lastSeenAt/idleExpiresAt/absoluteExpiresAt/revokedAt/isCurrent;
  gồm cả session đã revoke, không digest/raw cookie/AuthVersion. DELETE unknown và non-owner cùng 404;
  Guid sai shape → INVALID_REQUEST, Guid rỗng → VALIDATION_FAILED; revoke lại session đã revoke vẫn 204
  khi caller còn một access session hợp lệ. Xóa current session thì clear refresh cookie.
- Refresh module tắt: CSRF/Refresh/Logout trả 503; các route protected vẫn cần JWT trước khi gọi service.
  CORS chỉ exact configured origins, allow credentials + GET/POST/DELETE + Content-Type/Authorization/
  X-CSRF-TOKEN; expose X-Trace-Id/Retry-After. SameSite Strict dành cho browser cùng site.

## 6. Password recovery P08

- Module `Auth:PasswordRecovery:Enabled` mặc định false; bật cần Persistence + Email/SMTP và fixed
  HTTPS ResetUrl. Registration không bắt buộc bật. Public route input hợp lệ khi tắt trả 503;
  Change/Reauthenticate cần JWT trước. DTO errors/unknown JSON vẫn qua validation/binding chung.
- Forgot nhận EmailRequest, generic 202 như register/resend cho active/unknown/disabled/unverified/
  Google-only/cooldown/budget. Chỉ Active + verified + PasswordHash khác null được stage reset link.
  Lockout không chặn forgot/reset hợp lệ. Quota Recovery IP/account ở P04, cooldown 60 giây,
  tối đa 3 link/900 giây/account, token 1800 giây. Resend thay token trước trong transaction.
- Reset nhận token/newPassword trong JSON, không lấy token query/GET. Token 32 random bytes canonical
  Base64URL 43 ký tự, SHA-256 digest, bind ResetPassword/user/AuthVersion/expiry/one-time. Invalid,
  consumed, wrong purpose/version, expired hoặc account không eligible cùng 400 AUTH_CHALLENGE_INVALID.
- Reset success 204, clear refresh cookie; không JWT/auto-login. Hash mới + AuthVersion tăng + reset
  lockout counter + consume mọi outstanding challenge + revoke mọi session/refresh của target + audit/
  protected notification outbox cùng commit. MFA flag và recovery codes giữ nguyên, login vẫn cần MFA.
- Change nhận currentPassword/newPassword, actor/session/version từ validated JWT, không chọn user từ body.
  Verify current password qua gate P04 (lockout/audit); kiểm tra lại dưới user/session lock. New password
  phải theo policy và khác currentPassword. Password gate commit primary audit/counter trước transaction
  completion, giống Login; lỗi completion không xóa primary proof audit đã commit.
- Recent primary mặc định < 300 giây (exact boundary 300 bị từ chối), không future timestamp.
  Nếu MFA bật, MfaVerifiedAt cũng phải recent; current password không tự renew factor proof.
  Thiếu recent proof → 403 AUTH_STEP_UP_REQUIRED, không đổi hash/revoke. Refresh không renew proof.
- Reauthenticate nhận password, verify gate + fresh user/session/version, cập nhật chỉ AuthenticatedAt
  và audit Reauthenticated, trả 204. Không cấp JWT/phiên mới, không kéo dài idle/absolute, không đổi
  MfaVerifiedAt/AuthVersion. Thiếu recent MFA proof → 403; challenge/factor verification thuộc P09.
  Với access đã hết hạn, refresh tuần tự trước, sau đó reauthenticate khi cần primary proof.
- Change success 204, tăng AuthVersion, revoke tất cả session/refresh và consume challenge của actor,
  clear cookie + protected password-changed notification. Unknown/wrong/locked current password theo
  AUTH_INVALID_CREDENTIALS; session/version stale theo AUTH_SESSION_INVALID. Không ảnh hưởng user khác.
- Change/Reauthenticate áp quota Login IP + SQL account lockout; chúng không nhận email từ client để
  chọn partition/actor. Forgot/Reset không dùng ambient cookie để authorize; Change/Reauthenticate có
  X-CSRF-TOKEN + context cookie/origin khi module Refresh bật.
- Email P08 có outbox kind PasswordEmail và Data Protection purpose riêng, tách email verify P05.
  Reset link `ResetUrl#token=...`, client POST token; notification không chứa token/password/hash.
  Worker send sau commit với lease/retry, discard reset expired/replaced/consumed/version mismatch.
  Notification tham chiếu audit completion, gửi tới mailbox verified hiện tại; không suppress vì một
  password mutation mới hơn đã xảy ra. Delivery at-least-once, không bảo đảm recipient đã nhận.

## 7. RBAC matrix

| Hành vi | Anonymous | User | Admin |
|---|---|---|---|
| Register/Login/Recovery/Google start | Có, theo quota | Có, theo quota | Có, theo quota |
| Me/session/history cá nhân | Không | Của mình | Của mình |
| Xem user/role admin catalog | Không | Không | Có, MFA session |
| Gán role/disable user | Không | Không | Có, step-up + last-admin guard |
| Xem audit toàn hệ thống | Không | Không | Có, MFA session |
| Revoke session người khác qua route cá nhân | Không | Không | Không; cần flow admin riêng nếu bổ sung |

Admin không mặc nhiên bypass ownership mọi route. Policies lấy actor từ validated principal,
không request body. Mọi endpoint nghiệp vụ mới mặc định authenticated, policy explicit.
