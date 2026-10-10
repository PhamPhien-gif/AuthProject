# P12 — Login History, AuditLog và retention

Hoàn thành triển khai và automated regression 10/10/2026. P13 chờ lệnh.
Giữ một API project theo folder layers, EF Core/LINQ và xUnit hiện có.

Release regression: **169 unit + 151 HTTP/startup + 250 SQL = 570 passed**, 0 failed/skipped.
P12 bổ sung 32 test so với baseline P11 (538). Build Release không có warning/error.

## Đã triển khai

- GET users/me/login-history, owner SQL filter, UTC date/sort/paging bounded;
  GET admin/audit-logs require Admin.Read/MFA, actor/subject/event/outcome/reason/trace whitelist.
  DTO projection không expose credentials, raw metadata hoặc protected outbox.
- IP mask /24 hoặc /48 trước lưu login success/failure, không raw User-Agent mới;
  API bảo vệ cả legacy rows, retention scrub legacy PII theo batch.
- JSON RollingFileAppender từ XML + typed options, unique instance filename, size/backups/age policy.
  IErrorHandler bắt silent file failure; ghi lỗi không bị acknowledge thành công.
- AuditDispatcher claim bằng rowversion/lease, verify envelope khớp durable SQL audit,
  write ngoài transaction, ack chỉ khi lease/rowversion còn đúng. Backoff 30–900s,
  max-attempt dead-letter, safe Error alert, retained EventId cho deduplication.
- Retention opt-in: history/audit/processed outbox, expired challenge và terminal refresh families.
  User lock serialize với refresh/logout; live consumed refresh tombstone giữ nguyên dù token expired.
  Pending/dead-letter audit giữ lại; session có history FK chưa bị xóa; owned file cleanup không recursive.
- Worker/cleanup flags mặc định false, startup options validate dependencies/bounds.
  Migration AddAuditDeliveryState và SQL delta 004, không thay .env thật hoặc DB ứng dụng.

## TDD và kiểm chứng thực tế

Privacy cases viết trước helper (Red thiếu HistoryPrivacy); log4net silent failure test viết trước sửa sink,
Red Assert.Throws không thấy exception, Green với error handler. Ba SQL delivery tests viết trước
dispatcher, Red thiếu AuditDispatcher, Green sau implementation/migration.
HTTP/validation/retention/concurrency/file tests bổ sung sau implementation, không tuyên bố mọi test viết trước.
Lỗi test fixture dùng FakeTimeProvider lùi thời gian được sửa bằng clock riêng cho dữ liệu cũ;
không coi lỗi fixture là feature Red.

Test đã kiểm chứng: wrong owner, admin/MFA matrix, filter allowlist/paging overflow, metadata redaction,
real file write + exclusive file lock, lease concurrency, crash/lease-expiry retry với cùng EventId,
final-claim dead-letter, retry/backoff không mất SQL source, invalid payload không gửi,
live consumed tombstone, terminal chain delete không vi phạm FK, pending/dead-letter retention,
legacy IP/UA scrub và file cleanup chỉ owned filenames. Không gửi Google/SMTP thật.

## Migration và sử dụng

`20261010132626_AddAuditDeliveryState` thêm nullable DeadLetteredAt datetimeoffset và LastErrorCode varchar(32)
vào OutboxMessages. EF migration đã chạy trên SQL Express AuthProject_Test_GUID, snapshot không pending changes.
[004-P12-AuditDelivery.sql](Sql/004-P12-AuditDelivery.sql) là delta idempotent generated từ schema P10 để review;
script delta không được chạy lên database ứng dụng trong phiên này.

**Cần migrate trước startup bản P12, kể cả flags delivery/retention tắt**:

```powershell
dotnet tool restore
dotnet ef database update --project AuthProject --startup-project AuthProject
```

[Local mục 18](05-LOCAL-DEVELOPMENT.md#18-history-auditlog-và-retention-p12) có API, cấu hình,
ACL/read access, dead-letter query/requeue và retention opt-in. .env.example đã cập nhật, secret không commit.

## Lệnh test

```powershell
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --filter 'Category=Unit'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=None'
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --filter 'Dependency=SqlServer' --settings <sql-test.runsettings>
```

Full regression dùng runsettings ignored TestResults/P08/sql.runsettings; TRX tại TestResults/P12/Regression.
Test fixture chỉ create/drop AuthProject_Test_GUID trên server allowlist, không app DefaultConnection.
File tests dùng directory tạm, không file log production.

## Giới hạn

SQL audit là source query durable; file log là delivery channel, không tamper-proof/immutable archive.
At-least-once có duplicate khi crash sau write trước ack; ErrorHandler xác nhận lỗi phát hiện được,
không bảo đảm fsync hoặc không mất file sau power loss. ACL/inherited permissions do operator cấu hình.
Alerts là safe ILogger Error, không external notification integration; monitoring/production readiness P13.
Mỗi process phải dùng unique filename; default GUID có thể tạo file mới khi restart, age cleanup cần bật retention.
Pending/dead-letter tồn tại lâu hơn retention để điều tra/requeue; chưa có legal hold, backup/retention policy cần chốt.
Batch giới hạn số records/session families; xóa toàn token chain của terminal family có thể nhiều rows,
cần đo tải/locks P13. SMTP workers không đổi retry/dead-letter behavior trong bước này.
Không tự bật cleanup, không update DB ứng dụng, không sửa .env thật, không deploy hoặc triển khai P13.
