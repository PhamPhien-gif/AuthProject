# AuthProject

Backend Auth ASP.NET Core, SQL Server, EF Core + LINQ, tổ chức theo Layered Architecture
bằng folder trong một project ứng dụng `AuthProject`. Project xUnit tách riêng để kiểm thử.
Phạm vi hiện tại: **P04 — rate limiting, SQL lockout, trusted proxy và kiểm tra credentials**, chưa triển khai endpoint Auth.

## Đọc theo thứ tự

1. [Lộ trình và trạng thái từng bước](Docs/01-IMPLEMENTATION-PLAN.md).
2. [Kiến trúc, dữ liệu và quyết định bảo mật](Docs/02-ARCHITECTURE-AND-SECURITY.md).
3. [API, DTO và hợp đồng lỗi](Docs/03-API-CONTRACTS.md).
4. [TDD với xUnit và tiêu chí nghiệm thu](Docs/04-TDD-AND-ACCEPTANCE.md).
5. [Chạy local và cấu hình SQL Server](Docs/05-LOCAL-DEVELOPMENT.md).
6. [Kết quả triển khai P01 và bằng chứng TDD](Docs/06-P01-RESULTS.md).
7. [Kết quả P02 và cách mở rộng nền tảng API](Docs/07-P02-RESULTS.md).
8. [Kết quả P03, migration và cách dùng persistence/audit](Docs/08-P03-RESULTS.md).
9. [Kết quả P04, quota, lockout và trusted proxy](Docs/09-P04-RESULTS.md).

## Đã khởi tạo

- Giữ project API hiện có và Swagger chỉ bật trong Development.
- Tạo các folder Controllers, Services, Repositories, Entities, DTOs, Security trong `AuthProject`.
- Đưa UnitTestAuth vào solution, tham chiếu AuthProject; bỏ test rỗng và WeatherForecast của template.
- Thêm `.gitignore`, `.editorconfig`, `.env.example`, `global.json`.
- Chuyển sang `net10.0`, SDK 10.0.401; pin dependencies tập trung và lock files.
- Thêm AuthProject.IntegrationTests, WebApplicationFactory, fake clock/email/Google và fixture SQL Server.
- CI restore locked → build → unit → integration HTTP/SQL → audit dependencies; có TRX/coverage.

Đã có DTO/FluentValidation, ProblemDetails, exception handler, giới hạn request và typed options.
Đã có entities, DbContext/configurations, migration và SQL script idempotent, repository EF + LINQ,
IdentityV3 password hasher, audit SQL/outbox mã hóa và adapter log4net. Persistence và file sink
mặc định tắt; không tự migrate database khi startup. Worker delivery thuộc P12.
Chưa có endpoint Auth hay kết nối JWT/SMTP/Google thực tế.
Limiter IP/account/session đã có, cùng SQL lockout atomic, dummy hash và cấu hình trusted proxy.
Swagger chưa có operation nghiệp vụ. Các endpoint probe chỉ nằm trong project test.

## Kiểm tra khung project

Chạy từ thư mục chứa `AuthProject.sln` bằng .NET SDK 10.0.401 hoặc patch mới hơn cùng feature band:

```powershell
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --no-build --filter 'Dependency=None'
dotnet run --project AuthProject --launch-profile https
```

Swagger: `https://localhost:7148/swagger` (cần HTTPS development certificate được tin cậy).
Hai lệnh test trên không cần database. Chạy toàn solution sẽ bao gồm SQL integration test,
cần Docker hoặc cấu hình SQL Express test riêng theo [hướng dẫn local](Docs/05-LOCAL-DEVELOPMENT.md#6-chạy-test-p01).
Thiếu dependency SQL sẽ fail, không tự skip.

## Cách yêu cầu triển khai

Ví dụ: **“Triển khai P05 theo Docs, áp dụng TDD và báo kết quả kiểm tra.”**
Mỗi lần chỉ thực hiện bước được yêu cầu và dependency đã được cho phép.
P00–P04 đã hoàn tất; có **155 test pass** (68 unit, 68 HTTP/startup, 19 SQL).
API Development và EF CLI đọc `.env` local; xem [hướng dẫn](Docs/05-LOCAL-DEVELOPMENT.md#2-chuỗi-kết-nối-được-cung-cấp).
P05–P13 chờ lệnh.
