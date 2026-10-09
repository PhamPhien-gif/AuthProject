# P03 — Persistence, password hashing và audit nền tảng

Ngày: 09/10/2026. Trạng thái: **hoàn tất P03**. P04–P13 chờ lệnh.
Code ứng dụng vẫn nằm trong một project `AuthProject`, phân layer bằng folder.

## 1. Đã triển khai

| Thành phần | Kết quả |
|---|---|
| `Entities/` | User, Role, UserRole, AuthSession, RefreshToken, AuthChallenge, RecoveryCode, ExternalLogin, LoginHistory, AuditEvent, OutboxMessage |
| `Data/AuthDbContext.cs` | EF SQL Server, chuẩn hóa email/digest, UTC user timestamps qua TimeProvider, guard append-only audit/history |
| `Data/Configurations/` | Độ dài cột, unique/filtered indexes, restrictive FK, check constraints, rowversion |
| `Data/Migrations/` | InitialAuth + designer/snapshot; seed User/Admin có ID cố định, không seed tài khoản/password |
| `Repositories/` | User/Role/Session/RefreshToken repositories, EF + LINQ async, projection profile không chứa hash, AsNoTracking cho read |
| `Repositories/AuthUnitOfWork.cs` | Commit state + audit/outbox; rollback và clear tracker khi operation/save/commit lỗi |
| `Security/` | EmailNormalizer, IPasswordHashService/PasswordHashService dùng Identity PasswordHasher |
| `Logging/` | Typed audit contract/writer, Data Protection payload, adapter log4net và RollingFileAppender JSON |
| `Configuration/PersistenceServices.cs` | DI scoped store/repositories/unit of work, options validation, key store, sink flag |
| `.config/dotnet-tools.json` | Local dotnet-ef 10.0.12 |
| [SQL review](Sql/001-InitialAuth.sql) | Script idempotent từ InitialAuth, được test trên database rỗng và replay |

Packages mới: EF Core SQL Server/Design 10.0.12, log4net 3.5.0; cập nhật cả ba lock files.
Design dependency có `PrivateAssets=all`; không tạo thêm project layer.

## 2. Hành vi và invariant

- Email trim + ToUpperInvariant, unique NormalizedEmail dùng binary collation. Hai insert đồng thời
  chỉ một commit; không dựa vào kiểm tra tồn tại trước insert để bảo đảm uniqueness.
- Digest refresh/challenge/recovery là 64 ký tự hex viết hoa; schema không chứa plaintext token/OTP.
  SQL constraint chặn format không hợp lệ; cùng digest khác casing không tạo record trùng.
- User/session/refresh/challenge/recovery/outbox có SQL rowversion; stale update bị từ chối.
- RefreshToken có unique digest, self-FK ReplacedBy và filtered unique SessionId khi chưa consume/revoke.
  Index này không kiểm tra thời gian hết hạn; P07 phải xử lý token expired và thứ tự save khi rotation.
- ExternalLogin unique Provider/ProviderSubject và UserId/Provider; provider subject phân biệt casing.
- FK không cascade xóa history/audit. UserRole dùng PK composite. Audit SubjectId là reference generic,
  không FK cố định vì target có thể là user, session hoặc role.
- PasswordHasher IdentityV3: PBKDF2-HMAC-SHA512, 210000 iterations, salt 128 bit, subkey 256 bit.
  Giữ spaces/Unicode password; random salt cho mỗi hash; malformed hash trả Failed.
  NeedsRehash cho hash cũ, không yêu cầu hạ work factor của hash mạnh hơn.
- Audit metadata chỉ nhận session ID, authentication method và count; không có free-text/dictionary
  để đưa credentials vào log. TraceId phải là định danh do server tạo, không lấy từ client credentials.
- `IAuditWriter.Stage` chỉ stage AuditEvent và encrypted OutboxMessage vào cùng scoped DbContext;
  không tự save, không gọi log4net trước commit. SQL audit là dữ liệu query được; file sink là kênh logging.
- Outbox Data Protection dùng purpose `AuthProject.AuditOutbox.v1`; ciphertext không dùng làm nội dung log.
  Adapter log4net xuất eventId/UTC/typed record dưới dạng JSON một dòng; chặn trace newline injection.

Các repository `FindForUpdateAsync` trả tracked entity, **không tự lấy SQL update lock**.
ReadCommitted + rowversion là nền tảng P03; khóa/interleaving của rotation/revoke/last-admin thuộc P07/P11.
Unit of work chưa bật retry tự động; không replay delegate chứa side effect ngoài SQL.
Service phải validate khi được gọi ngoài HTTP và xử lý unique/concurrency theo use case.
HTTP response cho concurrent register chỉ kiểm chứng khi có endpoint ở P05.

## 3. Bằng chứng TDD

| Lát cắt | Red thực tế | Green |
|---|---|---|
| Hash adapter | 5 test fail vì NotImplementedException | Salt, PRF/iterations, verify/rehash và malformed hash pass |
| Audit protect/sink | 2 test fail vì NotImplementedException | Round-trip ciphertext, sai key/tamper bị chặn, MemoryAppender JSON pass |
| Migration/schema | Migration test fail vì GetMigrations rỗng; state/concurrency tests fail vì chưa có bảng | Migration, unique email đồng thời, rowversion, commit/rollback pass |
| Persistence/hash options | 2 test fail vì startup chưa từ chối bật persistence thiếu connection hoặc work factor yếu | ValidateOnStart từ chối với thông báo không lộ giá trị cấu hình |
| Digest casing | SQL test fail vì duplicate cùng digest khác casing được insert | Canonical uppercase + SQL check constraint, duplicate bị từ chối |

Sau Green bổ sung regression cho repository projection/tracking, unit-of-work rollback sau khi đã save,
refresh filtered index, restrictive FK, provider linking và invalid security counters/status.
Lỗi TLS NuGet/SQL hoặc cấu hình fixture sai không được tính là Red nghiệp vụ.

## 4. Kiểm chứng cuối cùng

| Kiểm tra | Kết quả |
|---|---|
| `dotnet restore AuthProject.sln --locked-mode` | Pass, audit dependencies được bật |
| `dotnet build AuthProject.sln --configuration Release --no-restore` | Pass, 0 warning/error |
| Unit Release | **44 passed**, 0 failed/skipped |
| HTTP/startup Release | **45 passed**, 0 failed/skipped |
| SQL Release | **16 passed**, 0 failed/skipped |
| Tổng | **105 passed**, tăng 29 test so với P02 |
| NuGet vulnerability report, direct + transitive | Cả ba project không có package bị advisory từ nguồn hiện tại |
| Migration/code snapshot | Không có pending model change; Migrate hai lần không nhân đôi role |
| SQL script | Apply vào database rỗng và replay lần hai; chỉ 2 role, không có user được seed |

SQL dùng `LAPTOP-LTSU53RG\SQLEXPRESS01`, Windows Integrated Security, catalog
`AuthProject_Test_<GUID>` do fixture tạo/xóa. Không dùng EF InMemory và không chạm database `AuthProject`.
Reports tại `TestResults/P03/Unit` và `TestResults/P03/Integration` (được ignore).
Lệnh chạy unit/integration giữ nguyên trong [hướng dẫn local](05-LOCAL-DEVELOPMENT.md#6-chạy-test-p01).

Microbenchmark Release trên runtime 10.0.12, warm-up và 20 mẫu/operation:
hash p50 **85,0 ms**, p95 **88,6 ms**; verify p50 **86,2 ms**, p95 **264,2 ms**.
Đây là số đo máy hiện tại, chịu ảnh hưởng scheduling/tải nền; chưa kết luận sức chịu tải production.
Harness đo tạm nằm trong `TestResults/P03/HashBenchmark`, không thuộc solution/source được version.

## 5. Cách dùng nền tảng ở bước tiếp theo

Service inject `IUserRepository`/`IRoleRepository`/`ISessionRepository`/`IRefreshTokenRepository`,
`IAuthUnitOfWork`, `IPasswordHashService` và `IAuditWriter` theo nhu cầu. Stage thay đổi và audit bên trong
`ExecuteAsync`, trả dữ liệu thành công sau commit. Một `SaveChangesAsync` đã có transaction EF cho batch;
`ExecuteAsync` bao trùm cả use case nhiều lần đọc/ghi/save. Không gọi email/Google/file sink trong delegate.

Actor/subject ID và typed reason do service xác định; không nhận role, identity hay event type từ request
như dữ liệu đáng tin cậy. Tránh serialize entity hoặc exception vào audit. Các lớp không phụ thuộc HttpContext.

Persistence mặc định `Enabled=false`; muốn dùng store cần `Persistence__Enabled=true` và
`ConnectionStrings__DefaultConnection`. Startup không mở SQL hoặc tự migrate. Design-time factory yêu cầu
connection từ environment hoặc `.env` local khi dùng EF CLI. Cấu hình/commands ở [mục migration local](05-LOCAL-DEVELOPMENT.md#4-database-và-migration-p03).

Data Protection application name là `AuthProject`; directory mặc định `data-protection-keys` dưới content root,
được ignore. Cần giữ keys còn dùng để giải mã outbox, cấp ACL phù hợp và cấu hình shared/encrypted key store
trước production. Test hosts dùng ephemeral provider riêng, không ghi key/log file thực tế.

`Audit__Log4Net__Enabled=true` cho phép khởi tạo file sink khi được resolve. Config JSON rolling:
`Logs/audit.jsonl`, 10 MB/file, 10 backup. Chưa có outbox worker ở P03; flag không tự phát event.
`ILog.Info` không phải durable acknowledgment; worker/retry/error handler/alert hoàn thiện ở P12.

## 6. Giới hạn và bước tiếp theo

- Chưa có Login/Register/GetMe/JWT/rotation/logout, MFA/Google, RBAC hay rate limiter.
- LoginHistory và challenge/session/outbox lifecycle mới có schema; logic nằm ở các bước tương ứng.
- Chưa chạy migration lên DB ứng dụng, chưa tạo Admin, chưa gọi SMTP/Google thật.
- Append-only guard chỉ ở application SaveChanges; không chống raw SQL/ExecuteUpdate hoặc người có quyền DB.
- File rollover theo size chưa là retention theo ngày; outbox worker/cleanup/retry thuộc P12.
- SQL Express local đã pass; Docker/Testcontainers và GitHub Actions chưa chạy trong phiên này.
- Benchmark/quyền SQL/key protection/config production và security gate tiếp tục ở P13.

**Bước tiếp theo: P04 — brute force protection, rate limiter, lockout và trusted proxy (chờ lệnh).**

## 7. Nguồn kỹ thuật đã kiểm tra

- [Identity PasswordHasher implementation](https://source.dot.net/Microsoft.Extensions.Identity.Core/PasswordHasher.cs.html).
- [EF Core concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).
- [EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions).
- [log4net 3.5.0](https://www.nuget.org/packages/log4net/3.5.0).

## 8. Bổ sung theo yêu cầu: đọc `.env` cho API và EF CLI

Lỗi `Set ConnectionStrings__DefaultConnection explicitly for EF tooling` xuất hiện vì factory ban đầu
chỉ đọc biến môi trường của process, không nạp file `.env`. Đã thêm `Configuration/LocalDotEnv.cs`
và nối vào Program trước DI/options, cũng như design-time factory trước khi tạo DbContext.

API nạp `.env` trong Development; EF CLI mặc định Development nếu chưa đặt environment.
Tìm file trong project rồi solution, giữ nguyên SQL backslash/equals/semicolon, hỗ trợ quotes/comment.
Environment và command-line config ưu tiên hơn file; không mutate process env, không thực thi shell,
không log giá trị, không nạp file ở Production/Testing, không copy file secrets ra build/publish.
Chi tiết format/ưu tiên/lệnh tại [hướng dẫn local](05-LOCAL-DEVELOPMENT.md#2-chuỗi-kết-nối-được-cung-cấp).

TDD: 12 test loader fail vì NotImplementedException trước implementation, sau đó pass;
bổ sung 3 startup test để kiểm chứng Development/Production/Testing.
Regression Release **120 passed** (56 unit, 48 HTTP/startup, 16 SQL), 0 failed/skipped,
build 0 warning/error. Không thêm dependency và không đổi schema/migration.
EF CLI sinh SQL script thành công từ `.env` hiện có khi không có biến connection trong process test;
không chạy database update. Reports tại `TestResults/DotEnv/` (ignored).
