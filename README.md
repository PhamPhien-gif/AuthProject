# AuthProject

Backend Auth ASP.NET Core, SQL Server, EF Core + LINQ, tổ chức theo Layered Architecture
bằng folder trong một project ứng dụng `AuthProject`. Project xUnit tách riêng để kiểm thử.
Phạm vi hiện tại: **P02 — DTO validation, lỗi thống nhất và cấu hình**, chưa triển khai nghiệp vụ Auth.

## Đọc theo thứ tự

1. [Lộ trình và trạng thái từng bước](Docs/01-IMPLEMENTATION-PLAN.md).
2. [Kiến trúc, dữ liệu và quyết định bảo mật](Docs/02-ARCHITECTURE-AND-SECURITY.md).
3. [API, DTO và hợp đồng lỗi](Docs/03-API-CONTRACTS.md).
4. [TDD với xUnit và tiêu chí nghiệm thu](Docs/04-TDD-AND-ACCEPTANCE.md).
5. [Chạy local và cấu hình SQL Server](Docs/05-LOCAL-DEVELOPMENT.md).
6. [Kết quả triển khai P01 và bằng chứng TDD](Docs/06-P01-RESULTS.md).
7. [Kết quả P02 và cách mở rộng nền tảng API](Docs/07-P02-RESULTS.md).

## Đã khởi tạo

- Giữ project API hiện có và Swagger chỉ bật trong Development.
- Tạo các folder Controllers, Services, Repositories, Entities, DTOs, Security trong `AuthProject`.
- Đưa UnitTestAuth vào solution, tham chiếu AuthProject; bỏ test rỗng và WeatherForecast của template.
- Thêm `.gitignore`, `.editorconfig`, `.env.example`, `global.json`.
- Chuyển sang `net10.0`, SDK 10.0.401; pin dependencies tập trung và lock files.
- Thêm AuthProject.IntegrationTests, WebApplicationFactory, fake clock/email/Google và fixture SQL Server.
- CI restore locked → build → unit → integration HTTP/SQL → audit dependencies; có TRX/coverage.

Đã có DTO/FluentValidation, ProblemDetails, exception handler, giới hạn request và typed options.
Chưa có endpoint Auth, entity, DbContext, migration hay kết nối JWT/SMTP/Google/log4net thực tế.
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

Ví dụ: **“Triển khai P03 theo Docs, áp dụng TDD và báo kết quả kiểm tra.”**
Mỗi lần chỉ thực hiện bước được yêu cầu và dependency đã được cho phép.
P03–P13 chờ lệnh; không tự triển khai tiếp sau P02.
