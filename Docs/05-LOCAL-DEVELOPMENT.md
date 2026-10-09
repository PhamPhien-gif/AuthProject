# Chạy local và cấu hình

## 1. Trạng thái hiện tại: P04

`global.json` chọn SDK **10.0.401** + latestPatch, mọi project target `net10.0` qua
`Directory.Build.props`. IDE phải hỗ trợ SDK này; CLI là cách build đã được kiểm chứng.
Phiên bản NuGet nằm ở `Directory.Packages.props`, dependency graph ở từng `packages.lock.json`.
API có Swagger, DTO/FluentValidation, ProblemDetails, exception handler, options và request limits;
đã có entities, DbContext/migration, repositories, password hasher, audit/outbox và log4net adapter;
chưa có Auth endpoint. Persistence và file sink mặc định tắt.
Rate limiting mặc định bật; đã có SQL lockout, credential gate và trusted proxy options.
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
