# Kết quả P06 — Login, JWT authentication và GetMe

Ngày: 10/10/2026. Trạng thái: **Hoàn tất P06**; P07–P13 chờ lệnh.
Code ứng dụng vẫn một project `AuthProject`, phân layer bằng folder.

## 1. Thay đổi

- `Controllers/LoginController.cs`: POST `/api/v1/auth/login` anonymous + Login rate policy;
  GET `/api/v1/auth/me` cần authentication, no-store và safe DTO.
- `DTOs/LoginResponses.cs`, `Services/LoginService.cs` và interface: AuthResult/MeResponse,
  orchestration từ credential gate P04 sang session completion, GetMe chỉ actor từ validated sub.
- `Repositories/LoginStore.cs` và interface: EF/LINQ profile/roles/session checks;
  session completion transaction, rehash, digest refresh dự phòng, LoginHistory và protected audit outbox.
- `Security/Authentication/`: RSA key loading/startup validation, RS256 token issuance,
  access-token claims/lifetime rules, JwtBearer callbacks, CLI key generator và anonymous Swagger filter.
- `Configuration/LoginServices.cs`, JwtOptions, Program: DI, JwtBearer, typed options,
  authentication trước policy limiter và authorization; `MapControllers().RequireAuthorization()`.
- `PasswordAttemptStore`: LoginHistory failure cùng counter/audit transaction, unknown user ID null;
  IP/UA lấy server HTTP context, UA tối đa 256 ký tự. Không log request body hoặc credentials.
- Swagger có Bearer definition, global controller requirement; AllowAnonymous operations override security rỗng.
  Test probes anonymous explicit, authorization probe chỉ thuộc test ApplicationPart.
- JwtBearer 10.0.12 và IdentityModel JWT 8.23.0 pin central + lock files; `.env.example` và Docs cập nhật.

Không thêm production project, không thêm migration mới; schema P03/P04 đã đủ.
Không sửa `.env` riêng, update database `AuthProject`, tạo key persistent hoặc gửi SMTP thật.

## 2. Authentication và authorization

Token chỉ nhận từ Authorization: Bearer, không query/cookie; header giới hạn 8192 ký tự.
JWT ký RS256, bắt buộc kid trùng active key ID và typ at+jwt. Validate signature/algorithm/issuer/audience/
nbf/exp; audience không bỏ qua trailing slash. Lifetime theo TimeProvider, skew mặc định 30 giây.
Claims bắt buộc không trùng: sub/sid/jti Guid khác rỗng, av ≥ 1, iat numeric không tương lai, amr=pwd.
Không đưa email/displayName/password/security secret vào JWT; role claims lấy lại từ SQL sau validation.

Mỗi bearer request join Session/User trong SQL kiểm tra ownership, cả hai AuthVersion, status Active,
email confirmed, session chưa revoke/còn idle + absolute lifetime, MFA phù hợp. Không cache state.
Sau đó load roles SQL vào principal; không tin role từ JWT/request. Default MVC controller auth được
kế thừa qua MapControllers, anonymous whitelist bằng AllowAnonymous. Route thiếu vẫn 404;
minimal API nghiệp vụ thêm sau phải khai báo RequireAuthorization tương ứng.

Invalid/missing token/session → 401 AUTH_UNAUTHENTICATED + WWW-Authenticate: Bearer;
role denied → 403 AUTH_FORBIDDEN. Session lookup DbException → safe 503 SERVICE_UNAVAILABLE,
không bypass DB check hoặc echo SQL/provider error. Request đã validate trước mutation commit
có thể đang chạy; thao tác nhạy cảm P11 cần kiểm tra lại trong transaction.

GetMe trả id/email/displayName/roles/emailVerified/mfaEnabled/createdAt, không dùng userId query để
chọn account khác, không hash/token digest/AuthVersion. Role management/admin MFA/step-up thuộc P11.

## 3. Login transaction, MFA và refresh giới hạn

Credential gate P04 validate input và verify hash/dummy hash, cập nhật lockout + primary-factor audit
trong transaction riêng. Wrong/unknown/locked/disabled/unverified/Google-only cùng generic 401;
failure history cùng commit. Primary factor đúng ghi PasswordVerified Pending, chưa login success.

LoginStore transaction mới khóa lại User (`UPDLOCK,ROWLOCK`), kiểm tra version/status/confirmed/lockout.
Legacy hash cần rehash được verify lại trước thay hash; hash mới, session, digest, history Success,
LoginCompleted audit/outbox cùng save/commit. Ký token trước save/commit; signing/audit/save failure
rollback transaction completion. Token chỉ trả sau commit. Primary-factor audit/counter đã commit
được giữ lại khi completion lỗi; không tuyên bố toàn request Login chỉ có một transaction.

Account đã bật MFA không có full session/token, ghi History Pending rồi trả 403 AUTH_STEP_UP_REQUIRED.
P09 mới tạo OTP challenge/enforce factor; P06 không giả challenge hoặc cho password bypass MFA.
LoginCompleted/History Success chỉ ghi khi full session commit.

Access token mặc định 300 giây; session idle 7 ngày, absolute 30 ngày, không extend LastSeenAt/idle ở P06.
Refresh digest tạo từ 32 random bytes, SHA-256 rồi zero/bỏ secret; đây là record dự phòng chưa có
refresh cookie/client secret dùng được. Không có endpoint refresh/logout/rotation/reuse/CSRF trong P06.
P07 phải nối issuance cookie/digest với rotation/logout trước public release.

## 4. Key/config và local

JWT mặc định tắt. Bật cần persistence, HTTPS issuer, audience, KeyId, RSA private PEM ≥ 2048 bit,
valid access/skew/idle/absolute limits. Startup lỗi key chỉ báo message an toàn, không raw PEM/path.
Private/public RSA objects được dispose khi host dừng. Module tắt không nạp key file hoặc mở SQL.
P06 một active key; multi-key rotation thuộc P13, đổi key sẽ khiến access cũ không validate.

CLI tạo local 3072-bit PEM bằng CreateNew, không overwrite hoặc in key:

```powershell
dotnet run --project AuthProject --no-launch-profile -- --generate-jwt-key AuthProject/keys/jwt-signing-key.pem
```

CLI không chạy API/worker, relative path theo working directory; API SigningKeyPath tương đối theo
content root. Unix create mode 0600; Windows cần ACL đúng identity/operator. PEM/key bị ignore và
chặn copy ra build/publish; production provision/mount key ngoài package. Chưa chạy CLI tạo key thật
trong workspace; tests tạo key tạm rồi xóa. Xem [hướng dẫn local](05-LOCAL-DEVELOPMENT.md#12-bật-loginjwtgetme-p06).

## 5. Bằng chứng TDD và regression

Red: 7 `AccessTokenRulesTests` fail NotImplementedException trước implementation.
Green: lifetime + required claim parser làm 7 test pass.
Red SQL: `Login_CreatesSessionDigestHistoryAndCompletedAudit` fail NotImplementedException tại store stub
trên database test. Green: session completion implementation làm test pass.
Sau đó bổ sung regression/failure cases; không tuyên bố mọi test mới đã chạy Red trước.
Compiler/environment/NuGet errors không tính là Red nghiệp vụ.

Lệnh Release đã chạy:

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| UnitTestAuth | 90 | 0 | 0 |
| HTTP/startup không cần SQL | 88 | 0 | 0 |
| SQL-dependent integration, gồm HTTP JWT với SQL | 81 | 0 | 0 |
| Tổng | **259** | **0** | **0** |

Thêm 63 test so với baseline P05 196: 13 unit + 7 HTTP/startup + 43 SQL-dependent.
Full integration cuối: 169 passed; unit: 90 passed. Reports cuối ở
`TestResults/P06/Unit/` và `TestResults/P06/FinalIntegration/`.

```powershell
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P06/Unit
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P06/Integration
dotnet list AuthProject.sln package --include-transitive --vulnerable --no-restore
```

Unit kiểm tra lifetime/claims/invalid values, RSA generator và không overwrite file.
HTTP/startup kiểm tra disabled Login/Me, private key missing/public-only/weak, session limits/issuer,
Swagger schema/bearer/anonymous override; baseline P01–P05 tiếp tục regression.
SQL + JwtBearer thật kiểm tra Login/GetMe ownership, generic failure/history, role 401/403/success,
SQL roles thay thế JWT roles, role changes, rehash, MFA không bypass, rollback signing,
stale primary AuthVersion, revoke/disable/user-session version/idle/absolute/JWT expiry,
signature/issuer/audience/slash/RS512/HS256/unsigned/kid/type/nbf/iat/missing claims/duplicate sid,
query token không được nhận, header quá dài và DB validation outage 503.

SQL dùng SQL Express + TestConnection/server allowlist, database GUID test riêng;
không fallback DefaultConnection. Key tạm và ephemeral Data Protection, không dùng key production;
fixture override module flags để không kế thừa `.env` Development bật SMTP/JWT thật.
Reports `TestResults/P06/` bị Git ignore. Docker/GitHub Actions chưa chạy; không SMTP/Google thực tế.

Restore locked-mode pass, build Release 0 warning/error; NuGet audit direct/transitive không thấy
advisory từ nguồn đang dùng ở thời điểm kiểm tra. Tham khảo
[Microsoft JWT bearer validation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0).

## 6. Bước tiếp theo

P07 — refresh issuance/cookie, rotation/reuse detection, logout/revoke/CSRF **chờ lệnh**.
Không phát hành public trước khi hoàn tất lifecycle này và security gates P13.
