# API, DTO và lỗi thống nhất

**P06 đã triển khai Register, Verify Email, Resend, Login và GetMe; các endpoint còn lại chờ bước tương ứng.** Prefix `/api/v1`.
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
| DELETE /auth/sessions/{sessionId} | Owner | 204; unknown/non-owner cùng 404 | P07 |
| POST /auth/password/forgot | Anonymous | EmailRequest → generic 202 | P08 |
| POST /auth/password/reset | Anonymous | ResetPasswordRequest → 204, không auto-login | P08 |
| POST /auth/password/change | Authenticated + step-up | ChangePasswordRequest → 204, revoke tất cả session | P08/P09 |
| POST /auth/reauthenticate | Authenticated | Password hoặc Google recent proof → step-up result/challenge | P08–P10 |
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
P06 chỉ trả access token và profile, chưa có refresh cookie/token dùng được; flow này thuộc P07.
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

## 5. RBAC matrix

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
