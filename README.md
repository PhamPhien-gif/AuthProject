# AuthProject

Backend Auth ASP.NET Core, SQL Server, EF Core + LINQ, tổ chức theo Layered Architecture
bằng folder trong một project ứng dụng `AuthProject`. Project xUnit tách riêng để kiểm thử.
Phạm vi hiện tại: **P12 — Register/Login/JWT/GetMe, refresh rotation/logout/session, password recovery, MFA Email OTP, Google OAuth/OIDC, quản trị RBAC và history/audit query/delivery/retention**.

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
10. [Kết quả P05, endpoint đăng ký/xác minh và cấu hình SMTP](Docs/10-P05-RESULTS.md).
11. [Kết quả P06, RSA key, Login/GetMe và kiểm chứng JWT](Docs/11-P06-RESULTS.md).
12. [Kết quả P07, rotation/logout/session/CSRF](Docs/12-P07-RESULTS.md).
13. [Kết quả P08, password recovery/change/reauthenticate](Docs/13-P08-RESULTS.md).
14. [Kết quả P09, MFA Email OTP và recovery](Docs/14-P09-RESULTS.md).
15. [Kết quả P10, Google/OIDC, linking và migration mới](Docs/15-P10-RESULTS.md).
16. [Kết quả P11, RBAC, profile và bootstrap Admin](Docs/16-P11-RESULTS.md).
17. [Kết quả P12, history/audit, log4net worker và retention](Docs/17-P12-RESULTS.md).

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
mặc định tắt; không tự migrate database khi startup. Audit delivery và retention đã có, mặc định tắt.
Đã có endpoint Register/Verify/Resend, MailKit SMTP adapter và worker email outbox.
Registration mặc định tắt; cần bật persistence/email và cấu hình đầy đủ để sử dụng.
Đã có JWT RS256 + SQL session validation mỗi request, MFA Email OTP và Google/OIDC với local MFA gate.
Login/JWT mặc định tắt, cần bật persistence và cấu hình RSA private key/issuer/audience.
Worker audit log4net có lease/retry/dead-letter; retention giữ pending audit và live refresh tombstone.
Limiter IP/account/session đã có, cùng SQL lockout atomic, dummy hash và cấu hình trusted proxy.
Swagger có các operation P05–P12, Bearer authorization và CSRF header. Các endpoint probe chỉ nằm trong project test.

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

Ví dụ: **“Triển khai P13 theo Docs, áp dụng TDD và báo kết quả kiểm tra.”**
Mỗi lần chỉ thực hiện bước được yêu cầu và dependency đã được cho phép.
P00–P12 đã hoàn tất; **570 test pass** (169 unit, 151 HTTP/startup, 250 SQL); xem [báo cáo P12](Docs/17-P12-RESULTS.md).
API Development và EF CLI đọc `.env` local; xem [hướng dẫn](Docs/05-LOCAL-DEVELOPMENT.md#2-chuỗi-kết-nối-được-cung-cấp).
P13 chờ lệnh. Cấu hình Google ở [local mục 16](Docs/05-LOCAL-DEVELOPMENT.md#16-bật-google-oauthoidc-p10).
Chạy `dotnet ef database update --project AuthProject --startup-project AuthProject` trước startup bản P12,
kể cả các worker đang tắt. Migration AddAuditDeliveryState, cấu hình và vận hành audit ở
[local mục 18](Docs/05-LOCAL-DEVELOPMENT.md#18-history-auditlog-và-retention-p12). Gate phát hành vẫn thuộc P13.
P11 không có migration mới. Bootstrap/enrollment/quản trị ở [local mục 17](Docs/05-LOCAL-DEVELOPMENT.md#17-rbac-và-bootstrap-admin-p11);
bootstrap không tự chạy khi startup và Admin mới phải enroll MFA trước khi dùng quyền quản trị.
