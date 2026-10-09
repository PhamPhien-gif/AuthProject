# AuthProject

Backend Auth ASP.NET Core, SQL Server, EF Core + LINQ, tổ chức theo Layered Architecture
bằng folder trong một project ứng dụng `AuthProject`. Project xUnit tách riêng để kiểm thử.
Phạm vi hiện tại: **P00 — khởi tạo và lập kế hoạch**, chưa triển khai nghiệp vụ Auth.

## Đọc theo thứ tự

1. [Lộ trình và trạng thái từng bước](Docs/01-IMPLEMENTATION-PLAN.md).
2. [Kiến trúc, dữ liệu và quyết định bảo mật](Docs/02-ARCHITECTURE-AND-SECURITY.md).
3. [API, DTO và hợp đồng lỗi](Docs/03-API-CONTRACTS.md).
4. [TDD với xUnit và tiêu chí nghiệm thu](Docs/04-TDD-AND-ACCEPTANCE.md).
5. [Chạy local và cấu hình SQL Server](Docs/05-LOCAL-DEVELOPMENT.md).

## Đã khởi tạo

- Giữ project API hiện có và Swagger chỉ bật trong Development.
- Tạo các folder Controllers, Services, Repositories, Entities, DTOs, Security trong `AuthProject`.
- Đưa UnitTestAuth vào solution, tham chiếu AuthProject; bỏ test rỗng và WeatherForecast của template.
- Thêm `.gitignore`, `.editorconfig`, `.env.example`, `global.json`.
- Giữ target `net8.0` và package sẵn có; kế hoạch chuyển .NET 10 LTS ở P01.

Chưa có endpoint Auth, entity, DbContext, migration, cấu hình JWT, SMTP, Google hoặc log4net.
Swagger hiện chưa có operation nghiệp vụ. Không có test nghiệp vụ nào ở P00.

## Kiểm tra khung project

Chạy từ thư mục chứa `AuthProject.sln` bằng .NET SDK 8.0.425 hoặc patch mới hơn cùng feature band:

```powershell
dotnet restore AuthProject.sln
dotnet build AuthProject.sln --no-restore
dotnet test AuthProject.sln --no-build
dotnet run --project AuthProject --launch-profile https
```

Swagger: `https://localhost:7148/swagger` (cần HTTPS development certificate được tin cậy).
Ở P00, `dotnet test` chỉ kiểm tra khả năng khởi chạy runner và báo chưa tìm thấy test;
không xem đây là bằng chứng Auth đã được kiểm thử.

## Cách yêu cầu triển khai

Ví dụ: **“Triển khai P01 theo Docs, áp dụng TDD và báo kết quả kiểm tra.”**
Mỗi lần chỉ thực hiện bước được yêu cầu và dependency đã được cho phép.
P01–P13 hiện đều chờ lệnh; không tự triển khai tiếp sau P00.
