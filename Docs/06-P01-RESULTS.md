# P01 — Toolchain và test harness

Ngày: 09/10/2026. Phạm vi: nền tảng kiểm thử, chưa triển khai P02 hoặc nghiệp vụ Auth.

## Thay đổi

- Một project ứng dụng `AuthProject` giữ nguyên phân layer bằng folder; hai project xUnit
  `UnitTestAuth`, `AuthProject.IntegrationTests`. Không có class library ứng dụng mới.
- Target .NET 10, SDK 10.0.401. Central package versions, per-project lockfile, audit cả dependency bắc cầu.
- `public partial Program` để test host truy cập entry point, không thêm endpoint test vào ứng dụng.
- WebApplicationFactory dùng HTTPS, disable auto-redirect, environment riêng và FakeTimeProvider.
  Host không kế thừa DefaultConnection; chỉ nhận DB từ fixture được kiểm soát.
- SQL fixture chọn SQL Express qua biến môi trường explicit hoặc Testcontainers khi không có biến;
  tạo catalog GUID mới và drop đúng catalog đã tạo, không chạy migration Auth.
- Shared source TestSupport chỉ compile vào tests: DatabaseTarget guard, email capture/failure,
  Google discovery/JWKS/token backchannel có chữ ký RS256 và one-time code, test identity builder.
- CI GitHub Actions đã đặt tại `../.github/workflows/auth-ci.yml` ở Git root:
  restore locked, build Release, unit, integration bao gồm SQL, audit, upload reports.

## Phiên bản pin

| Package | Version |
|---|---|
| Swashbuckle.AspNetCore | 10.3.0 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 |
| Microsoft.NET.Test.Sdk | 18.10.1 |
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 4.0.0 |
| coverlet.collector | 10.1.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 |
| Microsoft.Data.SqlClient | 7.1.1 |
| Testcontainers.MsSql | 4.16.0 |
| System.IdentityModel.Tokens.Jwt (test fake) | 8.23.0 |

Phiên bản được tra trực tiếp NuGet; giữ xUnit v2 để tránh migration major không cần thiết.
Không cài EF/JwtBearer/OIDC vào application trước khi module sử dụng được triển khai.

## Bằng chứng TDD và kiểm tra

| Kiểm tra | Kết quả |
|---|---|
| Red: DatabaseTargetTests trước implementation | 10 failed, 0 passed, đúng tại NotImplementedException |
| Red: FakeTransportTests trước implementation | 4 failed, 0 passed, đúng tại fake chưa implement |
| Green: unit suite | 14 passed, 0 failed, 0 skipped |
| HTTP host tests | 9 passed: Testing/Development/Production chạy; unknown route 404; Swagger chỉ Development |
| SQL Express integration (Debug và Release) | 1 passed mỗi lần: catalog riêng, transaction DDL rollback; fixture cleanup không lỗi |
| Restore locked | Thành công |
| Release build | Thành công, 0 warning/error |
| Coverage collector | Sinh TRX + Cobertura cho unit và HTTP suites |
| NuGet vulnerability audit | Không thấy vulnerable package theo nguồn hiện tại cho cả 3 projects |

Host tests là characterization của hành vi đã có từ P00, pass khi nối test harness;
không sửa code đúng thành sai để tạo Red giả. TDD Red/Green áp dụng vào các helper mới.
Lỗi TLS/SQL encryption trong sandbox được xử lý bằng chạy restore/audit/SQL test với quyền môi trường
phù hợp, không tắt TLS hoặc đổi Encrypt=False. Không tính lỗi môi trường đó là Red nghiệp vụ.

## Giới hạn và bước tiếp theo

- Docker daemon local không khả dụng; nhánh SQL container và workflow trên GitHub chưa chạy.
  Workflow đã được chuẩn bị cho runner Linux có Docker; cần xem kết quả khi push/PR sau này.
- Fake Google chỉ backchannel; state, nonce validation trong middleware, PKCE, consent/callback và
  linking được triển khai/test P10. P01 chưa có auth middleware để nối.
- Fake email chưa implement IEmailSender production vì interface thuộc P05; helper có capture/failure
  để nối vào lúc triển khai, không gửi SMTP thật.
- Không dùng DB `AuthProject`, không tạo entity/DbContext/migration, không gửi Google/email thật.
- P02 (DTO validation, lỗi thống nhất, configuration) tiếp tục chờ lệnh.

## Nguồn đối chiếu

- [Microsoft: integration testing và public Program](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0).
- [Testcontainers SQL Server module](https://dotnet.testcontainers.org/modules/mssql/).
- [Microsoft SQL Server image và digest](https://mcr.microsoft.com/en-us/artifact/mar/mssql/server/tag/2022-CU22-GDR1-ubuntu-22.04).
- [GitHub setup-dotnet: global.json và locked restore](https://github.com/actions/setup-dotnet).
