# Kết quả P04 — Rate limiting, lockout và trusted proxy

Ngày: 09/10/2026. Trạng thái: **Hoàn tất P04**, P05–P13 chờ lệnh.
Code ứng dụng vẫn nằm trong một project `AuthProject`, chia layer bằng folder.

## 1. Đầu ra

- `Configuration/BruteForceOptions.cs`, `BruteForceServices.cs`: quota có tên, lockout và trusted proxy;
  bind config, validate startup, đăng ký store/gate/factory.
- `Security/BoundedRateLimitStore.cs`, `RateLimitKeyFactory.cs`, `AuthRateLimitAttribute.cs`:
  fixed-window limiter memory có capacity cứng và acquisition đồng bộ; partition IP/account/session.
- `Middlewares/AuthRateLimitMiddleware.cs`: global quota trước đọc body; quota endpoint sau body limit;
  trả lỗi 429 theo ProblemDetails của P02, giữ Retry-After. Không đọc/log raw token, credentials.
- `Security/LockoutRules.cs`, `PasswordCredentialGate.cs`, interfaces và
  `Repositories/PasswordAttemptStore.cs`: kiểm tra password, trạng thái tài khoản, chống enumeration,
  counter SQL atomic và audit/outbox cùng transaction.
- `User.FailedLoginWindowStart`, migration `20261009145707_AddLockoutFailureWindow`,
  snapshot và [SQL incremental](Sql/002-P04-Lockout.sql).
- `Program.cs`, `appsettings.json`, `.env.example`, Docs và test cập nhật để dùng P04.

Không thêm dependency mới cho P04. `PasswordCredentialGate` validate `LoginRequest` bằng validator P02,
trả `PasswordAcceptance(UserId, AuthVersion, RequiresRehash)` cho bước tiếp theo;
chưa tạo JWT, session, endpoint Auth, email hay worker log4net.

## 2. Rate limit và proxy

Mỗi request dùng global API quota 200/60 giây/IP, kể cả route không tồn tại; sau đó áp dụng metadata
`[AuthRateLimit(AuthRatePolicy.Login)]` hoặc policy tương ứng trên controller/action trong bước sau.
Quota chi tiết nằm trong [cấu hình local](05-LOCAL-DEVELOPMENT.md#10-cấu-hình-và-kiểm-chứng-p04).
Đây là middleware với custom bounded store, không dùng `UseRateLimiter` của framework.

IP lấy từ connection sau forwarded-headers middleware. IPv4-mapped IPv6 được canonicalize;
không có remote IP thì dùng chung partition an toàn. Email trim/normalize uppercase rồi HMAC-SHA256,
key CSPRNG riêng mỗi process; dictionary không giữ email thô. Session chỉ lấy Guid `sid` từ principal
server đã xác thực, không lấy session ID/header/token chưa xác minh. Email extraction hiểu JSON
property không phân biệt hoa thường và `+json`, giữ body position cho MVC, malformed body vẫn do binder xử lý.

Giới hạn mặc định 10.000 partitions, key tối đa 128 ký tự; expire/reset theo TimeProvider.
Hết capacity trả 429, không xóa counter còn hiệu lực để cho attacker vượt quota. Không queue request.
Quota IP/account/session của policy được check rồi tăng cùng lock memory; global quota đã tăng trước
policy nên request bị policy từ chối vẫn tính vào global. Fixed window bắt đầu từ request đầu tiên;
burst tại ranh giới window vẫn có thể xảy ra. Retry-After là số giây nguyên, tối thiểu 1.

Trusted proxy mặc định tắt, không implicit trust loopback. Khi bật cần known proxy/network rõ ràng,
ForwardLimit 1–5; wildcard `/0` bị từ chối. Chỉ xử lý X-Forwarded-For và X-Forwarded-Proto,
bắt buộc số phần tử khớp nhau. Proxy không được tin cậy không thay IP/quota.
Proxy triển khai phải xử lý header client đúng cách; chỉ cấu hình IP của proxy bạn kiểm soát.

## 3. Password gate, SQL concurrency và audit

Input sai trả validation error P02. Input hợp lệ luôn qua password verification, kể cả unknown,
Google-only và account đang khóa; user không có password dùng dummy IdentityV3 hash cùng work factor.
Unknown/wrong/locked/disabled/unverified đều trả cùng `AUTH_INVALID_CREDENTIALS`, HTTP 401,
không tiết lộ tồn tại account hoặc thời điểm hết khóa. Dummy hash giảm chênh lệch chi phí hashing,
không bảo đảm toàn bộ response có thời gian tuyệt đối bằng nhau.

Sau verification ngoài transaction, store tạo DbContext mới, transaction SQL và đọc lại User với
`UPDLOCK, ROWLOCK`. So password hash + AuthVersion hiện tại với snapshot trước khi chấp nhận kết quả.
Credential đã đổi không bị chấp nhận bằng snapshot cũ, cũng không tăng counter của credential mới.
Scoped `AddDbContextFactory` vẫn cung cấp scoped DbContext cho repositories hiện có.

Mặc định 5 lần sai trong 900 giây → khóa 900 giây. Counter dừng ở ngưỡng; đang khóa không kéo dài
LockoutEnd dù password đúng/sai. Hết failure window reset count cho cửa sổ mới; hết khóa cho thử lại;
password đúng reset count/window. SQL state, AuditEvent và protected OutboxMessage cùng commit;
lỗi bảo vệ audit/save khiến rollback, không có counter tăng mà mất audit.

Sai password ghi `LoginFailed` với reason nội bộ; actor chưa xác thực để null, subject là user đã biết.
Chạm ngưỡng ghi thêm đúng một `AccountLocked`. Verify primary factor thành công ghi `PasswordVerified`
với outcome Pending, chưa ghi login hoàn tất và chưa thêm LoginHistory success. Rehash flag trả về
cho P06, chưa tự hash lại/save hash. P06 phải kiểm tra lại AuthVersion/trạng thái khi cấp phiên,
không coi acceptance là bằng chứng có hiệu lực vô thời hạn. Delivery log4net/outbox worker vẫn thuộc P12.

## 4. Bằng chứng TDD và regression

Red đầu tiên: 5 unit test limiter/lockout chạy trước implementation, thất bại với
`NotImplementedException`. Green: implementation quota/capacity/clock/lockout làm 5 test pass.
Red schema: 2 SQL concurrency/stale-verification test thất bại với PendingModelChangesWarning
trước migration P04. Green: thêm migration, counter song song và kiểm tra snapshot pass.
Sau đó thêm case HTTP/credential/audit rollback để mở rộng regression; không tuyên bố mọi case
đều đã chạy Red trước. Lỗi compiler/tooling không được tính là Red nghiệp vụ.

| Suite Release | Passed | Failed | Skipped |
|---|---:|---:|---:|
| UnitTestAuth | 68 | 0 | 0 |
| HTTP/startup integration | 68 | 0 | 0 |
| SQL Server integration | 19 | 0 | 0 |
| Tổng | **155** | **0** | **0** |

Build Release: 0 warning, 0 error. So với baseline sau dotenv 120 test: thêm 12 unit,
20 HTTP/startup và 3 SQL. Test mới kiểm tra:

- Burst memory/HTTP không vượt quota, expiry đúng boundary fake clock, capacity không bypass.
- Account normalize/cross-IP, JSON case và content type; các policy Login/Register/Otp/Refresh/Recovery/Google.
- Trusted/untrusted/disabled proxy, spoof X-Forwarded-For không đổi quota, global quota trên unknown route.
- Credential lỗi công khai giống nhau; unknown/Google-only verify dummy; success trả rehash flag.
- 20 password failure đồng thời: counter 5, một AccountLocked, đủ audit/outbox; correct password bị chặn
  khi đang khóa, exact cooldown boundary cho phép lại; snapshot cũ và audit failure rollback.
- Migration từ DB rỗng; cả script InitialAuth và P04 incremental được chạy lặp hai lần giữ đúng schema/history.

Các endpoint `_tests/p04/*`, socket/session filter giả chỉ thuộc integration-test assembly,
không đăng ký trong app runtime; không tuyên bố đã kiểm thử JWT authentication thực tế.

Lệnh đã chạy từ solution root:

```powershell
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P04/Unit
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P04/Integration
```

SQL tests chạy SQL Express với `ConnectionStrings__TestConnection` + server allowlist theo Docs/05,
database GUID riêng; không fallback DefaultConnection. TRX ở `TestResults/P04/` bị Git ignore.
Sau khi mở rộng test replay script incremental, build lại và chạy riêng 19 SQL test pass
(`TestResults/P04/SqlScript/`). Docker/GitHub Actions chưa chạy trong phiên này.

## 5. Migration cần áp dụng local

Chưa update database ứng dụng `AuthProject`. P04 thêm đúng một column nullable
`Users.FailedLoginWindowStart`; không seed tài khoản/admin password. Không cần tạo migration mới.
Sau khi `.env` đã có chuỗi kết nối và SQL hoạt động, chạy từ folder chứa `AuthProject.sln`:

```powershell
dotnet tool restore
dotnet ef database update --project AuthProject --startup-project AuthProject
```

EF design-time đọc `.env` Development, không cần bật Persistence flag cho lệnh update.
Nếu ở folder project con `AuthProject/`, dùng `dotnet ef database update`.
Script [001](Sql/001-InitialAuth.sql) là schema P03;
[002](Sql/002-P04-Lockout.sql) yêu cầu schema/migration history P03 có sẵn.
Để review SQL toàn bộ latest mà không ghi đè artifact ban đầu, xem Docs/05 mục 4.

## 6. Giới hạn và bước tiếp theo

Limiter quota nằm trong memory từng process; restart reset, nhiều instance không chia sẻ count/key.
Lockout nằm SQL nên shared giữa instance. Distributed/edge quotas và tuning/load test là gate P13.
Session limiter đã có hook principal đã xác thực; P06 phải đặt authentication trước middleware policy.
Anonymous refresh dùng cookie ở P07 phải áp dụng session quota sau verified token lookup;
chưa dùng raw refresh cookie làm partition. OTP challenge/email-send quota sẽ nối P05/P09.

P05 — Register/Verify Email/email transport **chờ lệnh**, chưa triển khai.
