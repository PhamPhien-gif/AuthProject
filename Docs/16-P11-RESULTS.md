# P11 — RBAC và quản trị

Triển khai 10/10/2026, giữ một project API theo folder layers. P12–P13 tiếp tục chờ lệnh.

## Đã triển khai

- AuthPolicies.AdminRead/AdminWrite, requirement + handler tập trung. Role lấy từ SQL mỗi request;
  handler/store kiểm tra SQL session, version, Active/verified, MFA enrolled + factor proof.
  Write cần primary/factor recent <300s theo EmailOtp options; signed JWT role/amr không thay được SQL.
- GET admin users phân trang/status/search với LINQ projection; GET role catalog User/Admin.
  PUT target roles và PATCH target status, DTO/route validation, ProblemDetails và Swagger/CSRF.
- Role/status mutation khóa quản trị, khóa actor/target, recheck quyền trong transaction,
  tăng AuthVersion/revoke sessions/refresh/challenges + audit/protected outbox cùng commit.
- SQL sp_getapplock owner Transaction bảo vệ last-active-Admin giữa các instance cùng database,
  timeout 5s; kiểm tra return code và rollback khi lỗi. Không xóa/rename protected roles,
  không self role/status mutation, không activate user chưa verified, no-op không revoke/audit giả.
- PATCH users/me chỉ displayName của actor; không userId/email/status/role mass assignment.
  Session APIs P07 giữ ownership, Admin không bypass.
- Command --bootstrap-admin đọc credentials từ process environment, validate và hash bằng service hiện có,
  tạo Active/verified User+Admin bằng quyền operator, chưa MFA, không session/token.
  Idempotent, không reset password/MFA, không adopt email của non-admin. Không public HTTP bootstrap.
- Audit events ProfileChanged/AdminBootstrapped dùng schema hiện có. Không migration/package mới P11;
  migration snapshot regression xác nhận không pending model changes.

## TDD thực tế

Viết trước 12 unit cases của AdminAccessRules và 4 SQL AdminStore cases: Red do chưa có rules/store,
Green sau implementation. Lần build unit đầu có lỗi nullable trong test đã sửa trước khi ghi nhận
Red đúng lý do thiếu AdminAccessRules. Lần chạy SQL sau implementation có fixture AuthVersion=0
vi phạm constraint; fixture đã sửa rồi 4 test pass. Không coi lỗi fixture là feature Red.

Các tests được bổ sung sau: HTTP role matrix/CSRF/DTO/ownership/revocation, forged role/MFA claims,
audit failure rollback, no-op/self mutation, CLI process và concurrent bootstrap, validation unit.
Không tuyên bố mọi test đều được viết trước implementation.

Concurrency dùng request/context độc lập: hai Admin disable lẫn nhau chỉ một mutation thành công,
request còn lại bị recheck session/quyền sau lock; một active Admin luôn còn. Sole active Admin
disable/demote trả AUTH_LAST_ADMIN. Hai CLI bootstrap cùng email chỉ tạo một user + một audit.
Audit protector lỗi rollback cả bulk revocation, role state và version; token cũ không mất ngoài transaction.

CLI tests chạy dotnet run --no-build trong child process, env Testing và DefaultConnection **của fixture test**,
không nạp .env, không chạy host/worker. Dùng password test riêng và assert output không lộ secret.
JWT/HTTP tests dùng RSA key test + fake clock; SMTP/Google không được gọi thật.

## Kiểm tra

```powershell
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=None'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=SqlServer' --settings <sql-test.runsettings>
```

Full regression dùng runsettings ignored tại TestResults/P08/sql.runsettings và reports TestResults/P11/Final.
Test DB chỉ AuthProject_Test_GUID trên server allowlist; không DefaultConnection của người dùng.
Release: **158 unit + 145 HTTP/startup + 235 SQL = 538 passed**, 0 failed/skipped, build 0 warning/error.
P11 thêm 49 test so với P10. Full integration 380 pass và unit 158 pass; không giảm/skip baseline tests.
CLI child process dùng cùng build configuration của test assembly, hỗ trợ Debug/Release.

## Cấu hình và sử dụng

Không flag Admin riêng; dùng JWT/Persistence và MFA enrollment/session hiện có. Không migration mới,
nhưng database phải có schema P10. Secret bootstrap chỉ process env AUTHPROJECT_BOOTSTRAP_ADMIN_EMAIL,
AUTHPROJECT_BOOTSTRAP_ADMIN_DISPLAY_NAME, AUTHPROJECT_BOOTSTRAP_ADMIN_PASSWORD, không đọc từ .env/appsettings.
[Local mục 17](05-LOCAL-DEVELOPMENT.md#17-rbac-và-bootstrap-admin-p11) hướng dẫn nhập password kín,
chạy command, enroll/login MFA và gọi API. .env.example và API contracts đã cập nhật.

## Giới hạn và quyết định

Operator bootstrap chịu trách nhiệm xác minh mailbox ngoài hệ thống; trusted operator/DB access
là authorization cho command. Admin mới vẫn không dùng API quản trị trước enrollment/login MFA.
Last-admin invariant tính Active + verified + role Admin, không phụ thuộc lockout/MFA enrollment;
MFA recovery/enrollment có flow riêng. Không có delete user hoặc custom role/permission editor.
Write self role/status thực sự bị từ chối; cần Admin khác. No-op được giữ idempotent.
Governance lock chỉ bảo vệ writers đi qua các use case ứng dụng, không chống thao tác sửa DB thủ công.
Read policy có thể đọc với proof cũ miễn MFA session vẫn hợp lệ; write phải step-up. Profile không đổi credentials.
Không bootstrap/update database ứng dụng của bạn hoặc sửa .env thật. Chưa gửi SMTP/Google thật,
chưa deploy; history/audit query/log4net delivery/retention P12 và production gate P13 chưa triển khai.
