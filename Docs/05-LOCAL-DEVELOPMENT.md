# Chạy local và cấu hình

## 1. Trạng thái hiện tại: P02

`global.json` chọn SDK **10.0.401** + latestPatch, mọi project target `net10.0` qua
`Directory.Build.props`. IDE phải hỗ trợ SDK này; CLI là cách build đã được kiểm chứng.
Phiên bản NuGet nằm ở `Directory.Packages.props`, dependency graph ở từng `packages.lock.json`.
API có Swagger, DTO/FluentValidation, ProblemDetails, exception handler, options và request limits;
chưa có Auth endpoint, DbContext hoặc migration.
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

Quote ngoài bảo toàn backslash với dotenv reader thông dụng. Tên biến dùng `__` để ánh xạ
`ConnectionStrings:DefaultConnection`. ASP.NET Core **không tự đọc `.env`**;
chỉ copy `.env.example` thành `.env` sẽ chưa cấu hình app. P00 không thêm dotenv loader.

Để dùng ngay bằng PowerShell (biến chỉ trong process terminal và process con):

```powershell
$env:ConnectionStrings__DefaultConnection = 'Server=LAPTOP-LTSU53RG\SQLEXPRESS01;Database=AuthProject;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;'
dotnet run --project AuthProject --launch-profile https
```

Giá trị trong PowerShell chỉ có **một** backslash. Trong JSON phải escape thành `\\`.
Không dùng `Invoke-Expression` để chạy nội dung `.env`. Nếu sau này cần tự load `.env`, làm ở bước
cấu hình với parser được kiểm tra, chỉ Development, không ghi đè environment do deployment cung cấp.
Ở P00 ứng dụng chưa đọc connection string để kết nối DB; hướng dẫn này chuẩn bị cho P03.

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
- P02 dùng environment hoặc secret store của deployment; chưa tự load `.env`, local JSON hay User Secrets.
  Module được bật nhưng thiếu trường cấu hình bắt buộc sẽ fail startup.
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

## 4. Database và migration ở bước sau

P03 mới thêm EF packages, DbContext và migration. Không chạy các lệnh sau ở P00:

```powershell
# Ví dụ sau khi đã có DbContext và dotnet-ef phù hợp runtime:
dotnet ef migrations add InitialAuth --project AuthProject --startup-project AuthProject --output-dir Data/Migrations
dotnet ef migrations script --idempotent --project AuthProject --startup-project AuthProject
# Review script và kiểm tra target database trước khi update:
dotnet ef database update --project AuthProject --startup-project AuthProject
```

Không tự `Database.Migrate()` mỗi lần startup production. Migration deploy tách riêng,
có backup/restore, tài khoản migration riêng; tài khoản API không cần quyền alter schema.
Unit test không cần DB. Integration test dùng biến riêng `ConnectionStrings__TestConnection`
và DB prefix `AuthProject_Test_`; không fallback sang DefaultConnection.

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
