# Chạy local và cấu hình

## 1. Trạng thái P00

Máy hiện có SDK 8.0.425, 9.0.305, 10.0.401. `global.json` chọn 8.0.425 + latestPatch để giữ
project `net8.0` hiện tại; việc nâng .NET 10/packages nằm ở P01.
API mới có Swagger/Controller middleware; chưa có Auth endpoint, DbContext hoặc migration.
Không cần kết nối SQL/SMTP/Google để build hoặc mở Swagger ở P00.

```powershell
dotnet --version
dotnet restore AuthProject.sln
dotnet build AuthProject.sln --no-restore
dotnet test AuthProject.sln --no-build
dotnet run --project AuthProject --launch-profile https
```

Swagger: `https://localhost:7148/swagger`. Trust development certificate nếu cần:
`dotnet dev-certs https --trust` (thao tác local của người phát triển, chưa tự chạy trong P00).
Profile HTTP `http://localhost:5220` chỉ dùng smoke scaffold; khi có cookie/token phải test HTTPS.
Mở solution sẽ thấy `AuthProject` và `UnitTestAuth`. Trong `AuthProject` có các folder
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
- Local override JSON chưa được Program.cs tự load; muốn dùng phải thêm provider rõ ràng ở P02.
- User Secrets có thể bổ sung ở P02; production dùng environment/secret store, fail-fast khi thiếu key.
- `.gitignore` hiện nằm tại Git root `D:\Github\AuthProject`, bao phủ cả thư mục solution con.
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
