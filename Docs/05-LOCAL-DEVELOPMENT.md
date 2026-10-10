# Chạy local và cấu hình

## 1. Trạng thái hiện tại: P06

`global.json` chọn SDK **10.0.401** + latestPatch, mọi project target `net10.0` qua
`Directory.Build.props`. IDE phải hỗ trợ SDK này; CLI là cách build đã được kiểm chứng.
Phiên bản NuGet nằm ở `Directory.Packages.props`, dependency graph ở từng `packages.lock.json`.
API có Swagger, DTO/FluentValidation, ProblemDetails, exception handler, options và request limits;
đã có entities, DbContext/migration, repositories, password hasher, audit/outbox và log4net adapter;
đã có endpoint Register/Verify/Resend và SMTP email worker. Registration, persistence và file sink mặc định tắt.
Rate limiting mặc định bật; đã có SQL lockout, credential gate và trusted proxy options.
Swagger có năm operation P05/P06 và Bearer; Login/JWT/GetMe đã có, Google/MFA/refresh/logout chưa triển khai.
JWT mặc định tắt cho đến khi đủ persistence, issuer/audience/RSA private key.
Không cần kết nối SQL/SMTP/Google để build hoặc mở Swagger; SQL chỉ cần cho nhóm test SQL.

```powershell
dotnet --version
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --no-build
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --no-build --filter 'Dependency=None'
dotnet run --project AuthProject --launch-profile https
```

Swagger: `https://localhost:7148/swagger`. Trust development certificate nếu cần:
`dotnet dev-certs https --trust` (thao tác local của người phát triển, chưa tự chạy trong P00).
Profile HTTP `http://localhost:5220` chỉ dùng smoke scaffold; khi có cookie/token phải test HTTPS.
Mở solution sẽ thấy `AuthProject`, `UnitTestAuth`, `AuthProject.IntegrationTests`. Trong `AuthProject` có các folder
Controllers, Services, Repositories, Entities, DTOs, Security; không tách layer thành class library.

## 2. Chuỗi kết nối được cung cấp

`.env.example` chứa mẫu local:

```dotenv
ConnectionStrings__DefaultConnection='Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
```

Code hiện tại đã có `Configuration/LocalDotEnv.cs` để nạp `.env` cho API **Development**
và EF CLI. Tên biến dùng `__` ánh xạ `ConnectionStrings:DefaultConnection`.
Đặt `.env` tại thư mục chứa solution `D:\Github\AuthProject\AuthProject` hoặc tại `AuthProject/`;
nếu cả hai có file thì file trong project được ưu tiên, không merge hai file.
Loader không đọc `.env.example`; file example chỉ là mẫu để copy khi chưa có `.env`.

Ưu tiên cấu hình API: appsettings/User Secrets → `.env` → environment → command-line;
giá trị bên phải ghi đè bên trái. Loader không thay đổi environment của process hoặc reload khi file đổi;
restart API/chạy lại EF sau khi chỉnh `.env`. API Production/Testing không nạp file này.
EF CLI mặc định Development khi chưa đặt environment; `DOTNET_ENVIRONMENT` rồi
`ASPNETCORE_ENVIRONMENT` có thể chọn môi trường rõ ràng. Nếu chọn Production, dùng environment/secret store.

Format hỗ trợ: `KEY=value`, `export KEY=value`, single/double quotes, dòng comment `#` và inline comment.
Giữ nguyên backslash, dấu `=` và `;` trong connection string. Không shell execution, không expand `${VAR}`,
không multiline hoặc giải mã `\n`/`\t`; `\"` trong double quotes được đọc thành dấu quote.
Value có `#` ở đầu hoặc cần giữ spaces nên đặt trong quotes. Lỗi cú pháp chỉ báo số dòng, không echo secret.

Với `.env` như trên, chỉ cần chạy `dotnet run --project AuthProject --launch-profile https`.
Bạn vẫn có thể override bằng PowerShell (biến chỉ trong terminal và process con):

```powershell
$env:ConnectionStrings__DefaultConnection = 'Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
dotnet run --project AuthProject --launch-profile https
```

Giá trị trong PowerShell chỉ có **một** backslash. Trong JSON phải escape thành `\\`.
Không dùng `Invoke-Expression` để chạy nội dung `.env`. Loader dùng configuration provider trong memory,
không thực thi file và không ghi đè environment do deployment cung cấp.
Ở P03 connection string được dùng cho DbContext khi bật `Persistence__Enabled=true`.
Swagger/startup không tự truy vấn hoặc migrate SQL; chỉ khai báo biến connection chưa bật persistence.

`Integrated Security=True` dùng Windows identity của process chạy API. IIS/service có identity khác
tài khoản bạn mở terminal; phải cấp quyền SQL tối thiểu cho identity đó. SQL Express instance phải tồn tại
và hoạt động. `TrustServerCertificate=True` dành cho local với certificate tự ký;
production dùng certificate hợp lệ và `TrustServerCertificate=False`.
Linux/container không tự dùng Windows Integrated Security của máy host; cấu hình credential phù hợp riêng.

## 3. Chính sách cấu hình/secrets

- `.env`, `.env.*`, `appsettings.Local.json`, `appsettings.*.Local.json`, key/cert files bị ignore.
- `.env.example` được version để hướng dẫn; không có JWT signing key/SMTP password/Google secret thật.
- `appsettings.json` và `appsettings.Development.json` vẫn version, chỉ chứa cấu hình không bí mật.
- Không ignore toàn bộ appsettings hoặc migration vì làm mất cấu hình/schema cần review.
- Sau P03 đã bổ sung dotenv loader cho local Development/EF CLI; production vẫn dùng environment/secret store.
  Chưa tự load appsettings.Local.json hoặc User Secrets. Module được bật nhưng thiếu cấu hình sẽ fail startup.
- `.env` trong project bị chặn copy ra build/publish output; không đóng gói file secrets cùng ứng dụng.
- Git root hiện là `D:\Github\AuthProject\AuthProject`; P02 đã đặt `.gitignore` tại root này
  để tiếp tục bỏ qua `.env`, IDE/build/test artifacts sau thay đổi vị trí Git của workspace.
  File đã được track từ trước không tự untrack khi thêm ignore; chưa thực hiện git rm/commit nào.

Danh sách cấu hình sẽ thêm khi triển khai module (không đặt fake value chạy được):

| Module | Cấu hình cần thiết |
|---|---|
| JWT | Issuer, Audience, SigningKey reference, KeyId, AccessTokenLifetime, ClockSkew |
| Session | IdleLifetime, AbsoluteLifetime, cookie settings |
| OTP | Pepper/key reference, expiry, max attempts, resend cooldown/limit |
| Email | SMTP host/port/TLS, sender, credential, allowed public link base URL |
| Google | ClientId, ClientSecret, CallbackPath, allowed completion URLs |
| Browser | AllowedOrigins, CSRF, trusted proxies/networks |
| Log4net | Sink path, rollover/retention, environment label |
| Workers | Outbox retries/lease, cleanup intervals/retention |

## 4. Database và migration P03/P04

Đã pin EF Core SQL Server/Design **10.0.12**, local tool `dotnet-ef` **10.0.12**.
Migration `InitialAuth` và `AddLockoutFailureWindow` đã có; không tạo lại migration này.
`Docs/Sql/001-InitialAuth.sql` là schema ban đầu; `002-P04-Lockout.sql` là incremental script,
chỉ dùng khi schema/migration history P03 đã tồn tại. EF update áp dụng các migration còn thiếu:

```powershell
dotnet tool restore
# .env đã có ConnectionStrings__DefaultConnection; không cần đặt lại $env:...
New-Item -ItemType Directory -Force TestResults/Sql
dotnet ef migrations script --idempotent --project AuthProject --startup-project AuthProject --output TestResults/Sql/AuthProject-Latest.sql
# Chỉ chạy nếu bạn chủ động muốn cập nhật DB này, sau khi review script/target và backup:
dotnet ef database update --project AuthProject --startup-project AuthProject
```

Không tự `Database.Migrate()` mỗi lần startup production. Migration deploy tách riêng,
có backup/restore, tài khoản migration riêng; tài khoản API không cần quyền alter schema.
Unit test không cần DB. Integration test dùng biến riêng `ConnectionStrings__TestConnection`
và DB prefix `AuthProject_Test_`; không fallback sang DefaultConnection.

**Chưa chạy update lên database `AuthProject` của bạn trong phiên triển khai.** Design-time factory đọc
`ConnectionStrings__DefaultConnection` từ environment hoặc `.env` local; không fallback demo connection.
Lệnh `migrations script` chỉ sinh SQL, không cập nhật DB. Khi thay đổi schema ở các bước sau,
tạo migration tên mới bằng `dotnet ef migrations add <Name> --project AuthProject --startup-project AuthProject --output-dir Data/Migrations`.

Để bật module persistence cho các flow sẽ triển khai sau:

```powershell
$env:Persistence__Enabled = 'true'
$env:Persistence__DataProtectionKeyDirectory = 'D:\Github\AuthProject\AuthProject\data-protection-keys'
dotnet run --project AuthProject --launch-profile https
```

Thiếu/sai cấu trúc connection string khi bật module sẽ fail startup với thông báo an toàn.
Directory key cần lưu bền vững và giới hạn ACL; local file keys chưa cấu hình encryption at rest,
không dùng nguyên cấu hình local để phát hành production. Không xóa keys còn bảo vệ outbox chưa xử lý.
`Security__PasswordHash__IterationCount` mặc định 210000; cho phép 210000–1000000.
File adapter có thể bật bằng `Audit__Log4Net__Enabled=true`, config ở `AuthProject/Logging/log4net.config`;
path `Logs/audit.jsonl` tính từ working directory. P03 chưa có worker gọi sink từ outbox;
bật flag này chưa tự phát event. Worker/retry/retention hoàn thiện ở P12.

## 5. Verification P00

Đã chạy ngày 09/10/2026:

| Kiểm tra | Kết quả |
|---|---|
| `dotnet restore AuthProject.sln` | Thành công |
| `dotnet build AuthProject.sln --no-restore` | Thành công sau khi gộp folder, 0 warning, 0 error; chỉ AuthProject và UnitTestAuth |
| `dotnet test AuthProject.sln --no-build` | Runner hoạt động, báo không có test; chưa có test nghiệp vụ |
| Development host + `/swagger/index.html` | HTTP 200 |
| `/swagger/v1/swagger.json` | HTTP 200, chưa có operation nghiệp vụ |
| `git check-ignore` | IDE/build/test output, `.env`, local config và logs bị ignore |
| File cần version | `.env.example`, Docs, appsettings mẫu và migration path không bị ignore |

Không kiểm tra database, SMTP hoặc Google ở P00 vì chưa triển khai các adapter.

## 6. Chạy test P01

### Không cần SQL/Docker

```powershell
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=None'
```

### SQL Express local (máy hiện tại)

Chạy với Windows identity có quyền tạo/xóa database test trên instance được chọn:

```powershell
$env:ConnectionStrings__TestConnection = 'Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject_Test_Local;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
$env:AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS = 'LAPTOP-LTSU53RG\SQLEXPRESS01'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Category=Integration'
```

`AuthProject_Test_Local` chỉ là template tên hợp lệ, **không cần tạo database này**.
Fixture luôn thay catalog bằng `AuthProject_Test_<GUID>`, tạo trước suite rồi drop khi dispose.
Không dùng hoặc xóa database có sẵn. Server phải khớp chính xác allowlist, nhiều server ngăn bởi `;`.
Sai catalog/allowlist/attach-file/failover bị từ chối trước kết nối. Lỗi permission/connection là fail,
không skip. Nếu process bị kill trước dispose, catalog GUID có thể còn lại: kiểm tra đúng tên/owner
trước khi dọn thủ công; không chạy wildcard drop database.

### Docker / CI

Khi không có `ConnectionStrings__TestConnection`, fixture bắt buộc dùng Testcontainers SQL Server,
không fallback DB ứng dụng. Docker daemon Linux containers phải chạy (trên Windows cần Docker Desktop
đã được cấu hình); SQL container cần đủ RAM. CI dùng Ubuntu 24.04 và image SQL Server 2022 được pin digest.

```powershell
Remove-Item Env:ConnectionStrings__TestConnection -ErrorAction SilentlyContinue
Remove-Item Env:AUTHPROJECT_TEST_SQL_ALLOWED_SERVERS -ErrorAction SilentlyContinue
dotnet test AuthProject.sln --configuration Release --no-build
```

Máy hiện tại chưa có Docker daemon hoạt động; nhánh SQL Express đã chạy thành công.
Nhánh container/GitHub Actions chưa được thực thi trong phiên này, không tính là đã pass CI.

### Reports, coverage và dependencies

```powershell
dotnet test AuthProject.sln --configuration Release --no-build --logger trx --results-directory TestResults --collect:'XPlat Code Coverage'
dotnet list AuthProject.sln package --include-transitive --vulnerable --no-restore
```

Lệnh toàn solution cần SQL như trên. TRX/Cobertura ở `TestResults/` được Git ignore.
Để nâng dependency có chủ đích: sửa `Directory.Packages.props`, chạy restore không locked,
review lockfile diff rồi kiểm tra lại bằng `--locked-mode`. CI coi NU1901–NU1904 là error khi restore;
không chỉ dựa vào exit code lệnh liệt kê vulnerability (lệnh list chủ yếu là báo cáo).

P02 đã đặt workflow tại Git root hiện tại `.github/workflows/auth-ci.yml`, cập nhật đường dẫn
solution/global.json/reports tương ứng. Workflow chạy restore locked → Release build
→ unit → HTTP + SQL integration → vulnerability report, upload TRX/coverage kể cả khi test fail.
CI không cần secret Google/SMTP/production SQL. Không tự push/dispatch workflow trong P01.

## 7. Cấu hình và test P02

Lệnh ở mục 6 giữ nguyên. Kết quả Release hiện tại: **34 unit + 41 HTTP + 1 SQL = 76 passed**,
0 failed/skipped. Reports lần kiểm tra P02 nằm trong `TestResults/P02/`.

`Api:MaxRequestBodyBytes` mặc định 16384, cho phép 1024–1048576 bytes; áp dụng cả body có độ dài
và body streaming không khai báo độ dài. `Api:RequestTimeoutSeconds` mặc định 15, cho phép 1–120 giây.
Đổi cấu hình qua environment và restart process:

```powershell
$env:Api__MaxRequestBodyBytes = '32768'
$env:Api__RequestTimeoutSeconds = '20'
dotnet run --project AuthProject --launch-profile https
```

Typed options tại `Auth:Jwt`, `Auth:Email`, `Auth:Google`, `Auth:EmailOtp` đều mặc định `Enabled=false`.
Bật module yêu cầu đủ cấu hình theo [báo cáo P02](07-P02-RESULTS.md); không có secret demo fallback.
Đây mới là binding/validation cấu hình, **chưa kích hoạt chức năng Auth**. Key file thật/SMTP/Google
được kiểm chứng khi triển khai module tương ứng; P02 không đọc private key hoặc kết nối provider.

`ValidateOnStart` kiểm tra lúc startup; không hỗ trợ tự thay đổi security options khi process đang chạy.
Không commit private key, Google secret, SMTP password hoặc OTP pepper vào appsettings/Docs.

## 8. Kiểm chứng P03

Lệnh unit/integration ở mục 6 giữ nguyên. Release hiện tại: **44 unit + 45 HTTP/startup + 16 SQL = 105 passed**,
0 failed/skipped; build 0 warning/error. Reports ở `TestResults/P03/`, chi tiết tại
[báo cáo P03](08-P03-RESULTS.md). Các SQL test bao gồm migration/script idempotent, seed roles,
unique email đồng thời, rowversion, FK/index/check constraints, LINQ profile và rollback audit/outbox.
HTTP/startup tests dùng ephemeral Data Protection và không mở SQL hoặc ghi log/key file thật.

Để chỉ chạy nhóm SQL sau khi đặt hai biến SQL test ở mục 6:

```powershell
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=SqlServer'
```

`dotnet tool restore` chỉ cần cho EF CLI; chạy test/build không cần gọi dotnet-ef.
NuGet dependency audit direct/transitive không có advisory ở lần kiểm tra P03.

## 9. Bổ sung dotenv sau P03

Sau yêu cầu đọc `.env`, đã thêm 12 unit + 3 startup integration test cho loader.
Tổng regression Release: **56 unit + 48 HTTP/startup + 16 SQL = 120 passed**, 0 failed/skipped.
EF CLI đã sinh script idempotent khi xóa biến `ConnectionStrings__DefaultConnection` trong process test,
đọc `.env` hiện có; không cập nhật database. Báo cáo regression tại `TestResults/DotEnv/`.

## 10. Cấu hình và kiểm chứng P04

Release: **68 unit + 68 HTTP/startup + 19 SQL = 155 passed**, 0 failed/skipped; build 0 warning/error.
Reports ở `TestResults/P04/`; [báo cáo P04](09-P04-RESULTS.md) ghi bằng chứng TDD và giới hạn vận hành.
Hai script SQL được áp dụng và replay trên database test riêng; lockout có test 20 request song song.
Lệnh test ở mục 6 giữ nguyên; `--no-build` chỉ chạy binary đã build, nên build lại sau khi sửa source.

`.env.example` đã thêm cấu hình P04. Nếu đã có `.env`, bổ sung key cần dùng và restart API;
không copy đè cấu hình riêng. Defaults:

| Policy | Quota IP | Quota account/session |
|---|---|---|
| Api (mọi request) | 200/60 giây | — |
| Login | 10/60 giây | 30/900 giây/account |
| Register | 5/900 giây | 5/900 giây/account |
| Otp | 10/60 giây | attempt/challenge sẽ nối P09 |
| Refresh | 60/60 giây | 30/60 giây/session đã xác thực |
| Recovery | 5/900 giây | 5/900 giây/account |
| Google | 10/60 giây | — |

Override ví dụ `BruteForce__RateLimit__Policies__Login__IpPermitLimit=10`,
`BruteForce__RateLimit__Policies__Login__IpWindowSeconds=60`; account/session dùng `WindowSeconds`.
`BruteForce__RateLimit__Enabled=true`, `MaxPartitions=10000` mặc định. Hết quota/capacity trả
ProblemDetails 429 + Retry-After; restart reset quota memory. Nhiều instance cần quota shared ở P13.
Lockout SQL: `BruteForce__Lockout__MaxFailedAttempts=5`, `FailureWindowSeconds=900`,
`DurationSeconds=900`; không gia hạn khóa bằng request thất bại trong thời gian bị khóa.

Proxy mặc định `TrustedProxy__Enabled=false`. Khi thực sự chạy sau proxy bạn kiểm soát,
bật flag và khai báo IP/subnet của proxy, ví dụ `TrustedProxy__KnownProxies__0=10.0.0.1`.
`ForwardLimit=1` mặc định; proxy phải gửi cả X-Forwarded-For và X-Forwarded-Proto với số phần tử khớp nhau,
xử lý header client để không giả địa chỉ nguồn. Peer không thuộc danh sách bị bỏ qua, không trust `/0`.

P04 chưa mở endpoint login/register/refresh thực tế. Credential gate cần persistence khi được dùng
ở P06: bật `Persistence__Enabled=true`, giữ Data Protection keys và apply migration ở mục 4.
EF update đọc `.env` Development dù persistence flag đang tắt; không cần tạo lại migration.

## 11. Bật Register/Verify/Resend P05

Release P05: **77 unit + 81 HTTP/startup + 38 SQL = 196 passed**, 0 failed/skipped;
build 0 warning/error, restore locked-mode pass, dependency audit không thấy advisory ở thời điểm kiểm tra.
Reports ở `TestResults/P05/`; [báo cáo P05](10-P05-RESULTS.md) ghi giới hạn và bằng chứng TDD.
P05 không có migration mới; database phải có schema P03/P04. Chưa tự update DB ứng dụng.

MailKit **4.17.0** đã pin và cập nhật lock files. Các flag mặc định tắt để không gửi email khi chưa
cấu hình. Trong `.env` hiện có, thêm/sửa cấu hình theo mẫu sau bằng thông tin SMTP của bạn:

```dotenv
Persistence__Enabled=true
Auth__Registration__Enabled=true
Auth__Email__Enabled=true
Auth__Email__Host='smtp.your-provider.example'
Auth__Email__Port=587
Auth__Email__UseTls=true
Auth__Email__Sender='no-reply@your-domain.example'
Auth__Email__Username='your-smtp-username'
Auth__Email__Password='your-actual-smtp-password'
Auth__Registration__VerificationUrl='https://your-client.example/verify-email'
```

Thay toàn bộ placeholder bằng cấu hình riêng, không commit `.env`. Production dùng secret store,
giữ Data Protection key directory bền vững/ACL đúng như mục 4. Không sửa/xóa key đang bảo vệ email pending.
Port 465 dùng SSL-on-connect; port khác yêu cầu STARTTLS, không fallback plaintext hoặc bỏ certificate validation.
Thiếu persistence/email/HTTPS URL hoặc giới hạn sai khi bật Registration sẽ fail startup với thông báo an toàn.
API khi module tắt: input hợp lệ trả 503; input sai vẫn nhận lỗi validation/model binding P02.

Restart API sau chỉnh config:

```powershell
dotnet run --project AuthProject --launch-profile https
```

Dùng Swagger để `POST /api/v1/auth/register` với email/password/displayName, nhận generic 202.
User mặc định role User, pending verification; email được worker gửi sau transaction commit.
Email chứa link `VerificationUrl#token=...`: URL phải là trang client do bạn kiểm soát.
Trang client đọc fragment và `POST /api/v1/auth/email/verify` với JSON `{"token":"..."}`.
Nếu chưa có frontend, lấy token từ fragment trong email và gửi bằng Swagger; backend chưa cung cấp
trang xác minh, GET link không tự consume token. Không đưa token vào query/request logs.
Verify thành công 204, replay/expired/invalid 400 AUTH_CHALLENGE_INVALID; chưa phát JWT/session.

`POST /api/v1/auth/email/resend` nhận email, cùng 202 cho unknown/verified/disabled/cooldown/budget.
Mặc định cooldown 60 giây, tối đa 3 email/account/900 giây (bao gồm Register), token hết hạn 3600 giây.
Resend thay token và vô hiệu các token verify cũ. Limiter P04 vẫn áp dụng và có thể trả 429.

Worker poll 5 giây, tối đa 10 message/lượt, lease 120 giây, send timeout 30 giây;
SMTP lỗi retry 30–900 giây, tối đa 10 attempts. User đã commit không bị mất/tạo thêm khi SMTP lỗi.
Email expired/replaced/consumed bị đánh dấu processed và không gửi; ProcessedAt có nghĩa xử lý xong,
không phải bằng chứng recipient đã nhận. Audit outbox vẫn chờ worker P12.
Record đạt 10 attempts vẫn pending, cần operator điều tra và requeue đúng ID sau sửa nguyên nhân;
không tự reset toàn outbox. Lease còn hạn được tôn trọng, lease hết hạn được reclaim.

Delivery at-least-once có thể gửi lặp khi process crash sau SMTP accept trước SQL ack;
stable Message-ID không bảo đảm SMTP provider deduplicate. Worker giữ SQL user lock trong send
để serialize với resend/verify; giới hạn throughput cần đo ở P13. SMTP/provider thật chưa được test;
test suite dùng fake transport, không gửi email ra ngoài.

## 12. Bật Login/JWT/GetMe P06

P06 không có migration mới. Database cần schema P03/P04 và user đã xác minh email.
Regression Release: **90 unit + 88 HTTP/startup + 81 SQL = 259 passed**, 0 failed/skipped;
build 0 warning/error, restore locked-mode pass, dependency audit không có advisory từ nguồn đang dùng.
Login không yêu cầu SMTP/Registration bật khi user verified đã tồn tại; đăng ký mới thì dùng P05.
Chưa tự tạo private key, sửa `.env` hoặc update DB ứng dụng trong phiên triển khai P06.

Tạo RSA private key local từ thư mục chứa solution bằng SDK hiện có, không cần OpenSSL:

```powershell
dotnet run --project AuthProject --no-launch-profile -- --generate-jwt-key AuthProject/keys/jwt-signing-key.pem
```

CLI tạo key 3072 bit, không in private key, không chạy API/worker và không ghi đè file đã tồn tại.
File PEM/key bị Git ignore và không copy vào build/publish. Giới hạn ACL của file/key directory
cho đúng tài khoản chạy API; production mount/provision key riêng ngoài publish output.
Đường dẫn CLI tính từ working directory; `SigningKeyPath` tương đối tính từ content root `AuthProject/`.

Trong `.env` hiện có, thêm/sửa các key này bằng issuer/audience/key ID riêng của bạn:

```dotenv
Persistence__Enabled=true
Auth__Jwt__Enabled=true
Auth__Jwt__Issuer='https://your-auth.example'
Auth__Jwt__Audience='your-api'
Auth__Jwt__SigningKeyPath='keys/jwt-signing-key.pem'
Auth__Jwt__KeyId='local-rsa-1'
Auth__Jwt__AccessTokenLifetimeSeconds=300
Auth__Jwt__ClockSkewSeconds=30
Auth__Jwt__SessionIdleLifetimeSeconds=604800
Auth__Jwt__SessionAbsoluteLifetimeSeconds=2592000
```

Issuer là HTTPS identifier cố định của hệ thống, không phải yêu cầu truy cập URL đó để validate;
JWT local không dùng discovery/JWKS remote. Audience là identifier của API; issuer/audience match exact.
Key ID tối đa 64 ký tự ASCII letter/digit/`-_.`; private key PEM RSA ≥ 2048 bit.
Thiếu key, public-only/weak key, thiếu persistence hoặc config sai sẽ fail startup với lỗi an toàn.
Restart khi đổi key/config; P06 chỉ có một active key, chưa có multi-key rotation.

Chạy API bằng HTTPS profile, mở Swagger:

```powershell
dotnet run --project AuthProject --launch-profile https
```

`POST /api/v1/auth/login` với email/password của user Active + verified → 200 accessToken/tokenType/
expiresIn/user. Không có refresh cookie ở P06. Trong Swagger, bấm Authorize và dán access JWT
(không cần gõ thêm `Bearer`), rồi gọi `GET /api/v1/auth/me`.
Không token hoặc token/session không hợp lệ → 401 ProblemDetails + WWW-Authenticate: Bearer;
Me chỉ trả profile/roles hiện tại của actor, không password hash/token digest/security version.
Token nhận qua Authorization header, không query/cookie; response thành công có no-store.

Sai/unknown/locked/disabled/unverified/Google-only cùng AUTH_INVALID_CREDENTIALS 401.
User đã bật MFA nhận AUTH_STEP_UP_REQUIRED 403 sau password đúng; chưa có OTP challenge/cấp phiên
đến P09, không có đường tắt bypass MFA. JWT module tắt: Login input hợp lệ 503, Me không token 401.
Role authorization cơ bản đã có, roles lấy từ SQL mỗi request; admin APIs/step-up thuộc P11.

SQL session validation kiểm tra status/email confirmation/AuthVersion/MFA/revoke/idle/absolute trên
mỗi bearer request. Lỗi DB lookup fail closed 503; không cache active state. Clock skew áp dụng JWT
nhưng session expiry dùng boundary UTC chính xác. P06 chưa extend idle/LastSeenAt theo request.
Rehash/session/digest/history success/audit completion cùng transaction; primary-factor counter/audit
đã commit trước đó theo P04, không rollback sự kiện primary factor khi completion lỗi.
History ghi failure cho password gate, Pending cho MFA primary factor, Success khi session commit;
history query API/log4net delivery vẫn thuộc P12.

Ở thời điểm P06, refresh chỉ có digest dự phòng; P07 đã thay bằng cookie/rotation/reuse/logout/CSRF.
Khi bật module Refresh theo mục 13, Login yêu cầu thêm CSRF header/context cookie.
Kết quả test và bằng chứng TDD: [báo cáo P06](11-P06-RESULTS.md), reports `TestResults/P06/`.

## 13. Bật Refresh/Logout/Sessions/CSRF P07

P07 dùng lại schema P03/P04, **không có migration mới và không cần chạy EF update riêng cho P07**.
Giữ cấu hình JWT/Persistence và RSA key ở mục 12; thêm vào `.env` hiện có:

```dotenv
Auth__Refresh__Enabled=true
Auth__Refresh__AllowedOrigins__0='https://localhost:7148'
# Chỉ thêm khi browser client thực sự chạy ở origin này:
# Auth__Refresh__AllowedOrigins__1='https://localhost:5173'
```

Origins là origin browser (scheme/host/port), không phải issuer/audience JWT. Chỉ HTTPS exact,
không wildcard, path, query, credentials hay dấu `/` cuối. HTTPS profile hiện tại là port 7148.
Restart API sau sửa `.env`. Flag mặc định false; bật thiếu JWT/origins sẽ fail startup an toàn.
Không chỉnh `.env` tự động hoặc update database ứng dụng trong phiên triển khai P07.

Thử bằng Swagger cùng HTTPS origin đã allowlist:

1. Gọi `GET /api/v1/auth/csrf`; browser giữ context cookie HttpOnly, response có `requestToken`.
2. Copy requestToken vào ô header `X-CSRF-TOKEN` của Login; nhập email/password user Active/verified.
   Login trả JWT và đặt refresh cookie HttpOnly/Secure/Strict/Path=/api/v1/auth, không trong JSON.
3. Authorize bằng access JWT để gọi Me/Sessions. Refresh/Logout không cần access token còn hạn;
   browser phải giữ cookie và gửi header CSRF trên từng POST. Swagger đã có ô nhập header.
4. Gọi Refresh tuần tự → JWT mới + cookie mới. Không gửi refresh song song: replay token đã dùng
   sẽ revoke toàn family, cả access/successor của lần thắng, và trả generic 401.
5. Logout → 204, clear cookie; access cũ gọi Me sau revoke commit → 401.
   Logout-all cần access JWT + CSRF, thu hồi mọi session của actor và clear cookie.
6. GET Sessions nhận page/pageSize; DELETE session cần JWT + CSRF, chỉ owner, non-owner/unknown cùng 404.

Swagger gửi Referer cùng origin; browser POST thường gửi Origin. Nếu client CLI/test không tự gửi
hai header này, thêm `Origin: https://localhost:7148` và giữ cookie jar. Không đưa cookie/token vào URL/log.
CSRF token bind context cookie, không bind access identity, nên có thể dùng sau login hoặc JWT hết hạn.
Thiếu/sai context cookie, CSRF header hoặc origin → 403 AUTH_CSRF_INVALID, không consume/revoke refresh.

Ví dụ client browser cùng site, gọi từ origin HTTPS đã cấu hình:

```javascript
const api = 'https://localhost:7148';
const csrfResponse = await fetch(`${api}/api/v1/auth/csrf`, { credentials: 'include' });
if (!csrfResponse.ok) throw new Error('Cannot obtain CSRF context');
const { requestToken } = await csrfResponse.json();
const response = await fetch(`${api}/api/v1/auth/login`, {
  method: 'POST', credentials: 'include',
  headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': requestToken },
  body: JSON.stringify({ email, password })
});
// Check response.ok; keep accessToken in memory. Refresh uses the same CSRF header and credentials.
```

CORS allow credentials chỉ cho origins trong danh sách. SameSite=Strict dành cho browser cùng site
(localhost khác port qua HTTPS phù hợp); allowlist không tự khiến browser gửi Strict cookie khác site.
Chưa thay SameSite hoặc triển khai frontend/BFF/native transport. Browser cookie policy/TLS/real proxy
vẫn cần smoke test khi deploy; WebApplicationFactory không mô phỏng toàn bộ browser.

Idle 7 ngày được kéo dài chỉ khi refresh thành công, absolute 30 ngày không kéo dài. JWT và cookie
được cap theo thời gian session còn lại. Quota refresh mặc định 60/phút/IP và 30/phút/verified session;
429 có Retry-After và giữ cookie/token để retry sau cooldown. Không retry song song hoặc dùng token cũ
sau response refresh thành công. Response bị mất sau commit có thể buộc login lại (strict reuse).
Module tắt: Login access-only P06, không refresh cookie; CSRF/Refresh/Logout trả 503.
Audit state/outbox SQL đã ghi; worker phát log4net vẫn P12. Xem [kết quả P07](12-P07-RESULTS.md).

## 14. Bật password recovery/change/reauthenticate P08

P08 dùng schema/challenge/outbox hiện tại, thêm enum string PasswordEmail/PasswordResetRequested/
Reauthenticated; **không có migration mới, không cần EF update riêng cho P08**.
Giữ Persistence, SMTP và JWT/Refresh config nếu thử toàn bộ flow; Registration không cần bật.
Thêm vào `.env` hiện có rồi restart API:

```dotenv
Auth__PasswordRecovery__Enabled=true
Auth__PasswordRecovery__ResetUrl='https://your-client.example/reset-password'
Auth__PasswordRecovery__TokenLifetimeSeconds=1800
Auth__PasswordRecovery__ResendCooldownSeconds=60
Auth__PasswordRecovery__SendWindowSeconds=900
Auth__PasswordRecovery__MaxSendsPerWindow=3
Auth__PasswordRecovery__RecentAuthenticationSeconds=300
```

ResetUrl phải là trang client HTTPS bạn kiểm soát, không userinfo/query/fragment; thay placeholder.
Backend chưa có trang frontend reset. Email link `ResetUrl#token=...`; client đọc fragment rồi POST JSON.
Worker P08 dùng Auth__Registration__PollSeconds/SmtpTimeoutSeconds/LeaseSeconds (mặc định 5/30/120)
kể cả Registration flag false; các limits được validate khi recovery bật. SMTP/DP keys ở mục 11/12.
Không đọc token qua query hoặc chạy GET để consume. Không in token/password/SMTP secret vào logs.

Thử trong Swagger:

1. POST `/api/v1/auth/password/forgot` với `{"email":"your-verified-email"}` → generic 202,
   giống nhau cho account không tồn tại, Google-only, disabled/unverified/cooldown/budget.
2. Lấy token từ fragment của link trong email, POST `/api/v1/auth/password/reset`:
   `{"token":"<token>","newPassword":"<new-password>"}` → 204. Không auto-login; access/refresh
   cũ bị revoke. Token chỉ dùng một lần, 30 phút mặc định; link mới invalidate link cũ.
3. Login lại bằng password mới. Nếu MFA bật thì vẫn cần MFA (flow/challenge P09); reset không tắt MFA.
4. Change password cần JWT của actor + current password và primary authentication recent < 5 phút.
   Nếu Refresh bật, lấy CSRF theo mục 13 và gửi X-CSRF-TOKEN/context cookie/origin.
   POST `/api/v1/auth/password/change` với `{"currentPassword":"...","newPassword":"..."}` →
   204, revoke mọi session của actor, clear cookie, gửi thông báo bảo mật qua outbox.
5. Khi nhận AUTH_STEP_UP_REQUIRED do primary proof quá cũ, gọi
   POST `/api/v1/auth/reauthenticate` với `{"password":"..."}` + JWT/CSRF → 204,
   rồi Change. Nếu JWT hết hạn, refresh tuần tự trước; refresh không tự renew primary proof.
   Password reauthenticate không renew factor proof: MFA proof missing/expired vẫn 403, cần P09.

Password mới 15–128 ký tự theo validator; Change không cho giống current password.
Actor/session/version lấy từ JWT, không gửi userId/sessionId/role trong JSON. Unknown properties bị reject.
Current password sai dùng credential gate/SQL lockout P04; 5 failure/900 giây → lock 900 giây,
Login IP quota áp dụng Change/Reauthenticate. Forgot/Reset dùng Recovery quota IP/account;
giới hạn gửi SQL default 3/900 giây, cooldown 60 giây, không reset budget khi thay link.
Input hợp lệ khi module tắt: Forgot/Reset 503; protected routes vẫn cần JWT trước. DTO/binding lỗi 400.

Reset success reset lockout counter, tăng AuthVersion, consume outstanding challenges và revoke sessions,
giữ MFA flag và recovery codes. Password notification không chứa token/password/hash; link và notification
được encrypt trong outbox PasswordEmail với purpose DP riêng. Worker retry/backoff tối đa 10 attempts,
lease rowversion, discard stale reset links. Notification được stage cùng transaction với hash/audit/revoke;
SMTP lỗi sau commit không làm mất password change. Delivery at-least-once có thể gửi lặp khi crash sau SMTP
accept trước DB ack; pending đạt 10 attempts cần operator điều tra/requeue, không tự xóa.

Không sửa `.env`, update database ứng dụng hoặc gửi SMTP thật trong phiên triển khai P08.
Regression Release **121 unit + 109 HTTP/startup + 139 SQL = 369 passed**, 0 failed/skipped,
build 0 warning/error. Reports ignored tại TestResults/P08; [báo cáo P08](13-P08-RESULTS.md).
