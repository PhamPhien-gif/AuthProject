# TDD, xUnit và kiểm soát chất lượng

Tài liệu này là kế hoạch kiểm thử. **P08 đã có test các flow P05–P07 và password recovery/change/reauthenticate**, MFA vẫn chờ bước tương ứng;
không dùng kết quả test nền tảng để báo hệ thống Auth đã hoàn tất.
Xem [bằng chứng P01](06-P01-RESULTS.md) và [lệnh chạy local](05-LOCAL-DEVELOPMENT.md#6-chạy-test-p01).
P02: [bằng chứng Red/Green và giới hạn](07-P02-RESULTS.md).
P03: [44 unit + 45 HTTP/startup + 16 SQL, Red/Green và migration](08-P03-RESULTS.md).
Bổ sung dotenv sau P03: 12 unit + 3 startup integration; tổng tại thời điểm đó 120 test.
P04: [bằng chứng quota/lockout/proxy và Red/Green](09-P04-RESULTS.md), tổng 68 unit + 68 HTTP/startup + 19 SQL.
RATE-01/02/03 đã kiểm chứng ở P04 qua probe chỉ nằm trong test assembly; endpoint login thực tế thuộc P06.
P05: [Red/Green, SMTP retry và concurrency](10-P05-RESULTS.md). REG-01/02, EMAIL-01/02 đã có
SQL/HTTP test; SMTP dùng fake transport, không tính là đã gửi email thật hoặc kiểm chứng provider thực tế.
Regression Release P05: **77 unit + 81 HTTP/startup + 38 SQL = 196 passed**, 0 failed/skipped.
P06: [bằng chứng TDD/JWT thật/SQL concurrency](11-P06-RESULTS.md). LOGIN-01/JWT-01/ME-01 đã có
test; role matrix cơ bản và session revoke test dùng test-only controller/SQL mutation, chưa phải admin/logout API.
Regression P06: **90 unit + 88 HTTP/startup + 81 SQL = 259 passed**, 0 failed/skipped.
P07: [bằng chứng rotation/reuse/concurrency/CSRF và rollback](12-P07-RESULTS.md).
SQL thật kiểm tra parallel refresh và refresh-vs-logout, cùng owner-scoped revoke/logout-all;
HTTP kiểm tra cookie/CSRF/CORS, access expiry/revocation và session quota sau verified cookie lookup.
Test cookie qua HTTP không thay thế smoke test policy cookie/TLS trong browser thực tế.
P08: [kết quả password lifecycle, rollback và concurrency](13-P08-RESULTS.md).
Reset token purpose/version/expiry/replay, concurrent reset/forgot và reset-vs-refresh kiểm tra trên SQL thật;
HTTP chứng minh access/refresh cũ bị chặn, current-password/recent-auth/CSRF enforcement và giữ MFA sau reset.

## 1. Chu trình cho mỗi lát cắt nghiệp vụ

1. Chọn một acceptance case trong bước đang được giao, mô tả input/state và observable output.
2. Viết xUnit `[Fact]`/`[Theory]`, Arrange → Act → Assert; tên `Method_WhenCondition_ExpectedResult`.
3. Chạy test và ghi bằng chứng **Red đúng lý do**. Lỗi restore/môi trường không tính là Red nghiệp vụ.
4. Viết implementation tối thiểu để Green; không mở rộng trước sang bước khác.
5. Refactor cấu trúc, tránh trùng logic; chạy lại suite liên quan.
6. Thêm failure/edge/concurrency case cần thiết, lặp lại Red → Green.
7. Trước khi báo Done, chạy toàn unit + integration thuộc module và build solution;
   cập nhật Docs với số pass/fail/skip thực tế và cấu hình test.

Không viết tất cả implementation rồi mới thêm test và gọi là TDD. Không tạo test khẳng định constant,
tên folder hoặc mock gọi đúng dòng code; ưu tiên kết quả nghiệp vụ và trạng thái persistence.
Không unit-test thuật toán PBKDF2 của framework, chỉ contract adapter, salt, verify, rehash và options.

## 2. Phân tầng test

| Loại | Project | Kiểm tra | Dependency |
|---|---|---|---|
| Unit | UnitTestAuth | Entity invariants, service branches, DTO validation, security, error mapping | Fake repo/email/audit/clock |
| HTTP integration | AuthProject.IntegrationTests | Binding, auth middleware, status/header/cookie/schema, authorization | WebApplicationFactory + test host |
| SQL integration | AuthProject.IntegrationTests | Constraints, LINQ, transaction, migration, concurrency, outbox | SQL Server riêng |
| Security regression | Integration + unit tùy case | Replay, bypass, IDOR, leakage, CSRF, lockout | Fake provider, real SQL khi stateful |
| Manual staging | Checklist P13 | Google real consent, SMTP delivery, TLS/browser cookie, key rotation | Staging credentials |

Không dùng EF InMemory để chứng minh unique index/transaction/rowversion/collation của SQL Server.
Nếu dùng repository fake ở unit test, vẫn phải có SQL integration test riêng cho query và race.
[EF Core testing strategy](https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy).

Fake `TimeProvider`, deterministic random adapter khi test state flow; không sleep để chờ token hết hạn.
Security randomness adapter thật có contract test riêng, không seed cố định trong production.
HTTP tests dùng fake signing keys dành cho test, không đọc key production.
Provider fake phải test cả thất bại validation/state/nonce; không mock bỏ toàn bộ auth middleware
ở test được tuyên bố kiểm chứng authentication.

## 3. Ma trận bắt buộc

| ID | Bước | Acceptance case |
|---|---|---|
| ENV-01 | P01 | Host chạy; Development có Swagger, Production không có |
| VAL-01 | P02 | Empty/null/oversized/invalid email/password/OTP/query nhận field errors thống nhất |
| ERR-01 | P02 | JSON hỏng, exception, 401/403/404/405/415/429 đều đúng ProblemDetails; headers vẫn còn |
| ERR-02 | P02 | 500 không lộ stack/SQL/secret, traceId nối được với log |
| DB-01 | P03/P05 | Concurrent email insert chỉ một User ở P03; mapping response register ở P05 |
| DB-02 | P03 | Migration tạo schema từ DB rỗng và rollback transaction không để state nửa chừng |
| HASH-01 | P03 | Same password → hash khác do salt; correct/wrong verify; rehash giữ password đúng |
| AUD-01 | P03 | State và audit/outbox cùng commit/rollback; payload không chứa secret trong log |
| RATE-01 | P04 | Vượt quota trả 429 + Retry-After; reset window theo fake clock |
| RATE-02 | P04 | Spoof proxy headers không vượt IP limit; account counter atomic khi song song |
| RATE-03 | P04 | Locked/unknown/wrong password cùng public response; lockout hết hạn được thử lại |
| REG-01 | P05 | Register valid chỉ User role, hash password, pending verification, 202 |
| REG-02 | P05 | Email khác hoa thường/duplicate cùng generic result; role injection bị chặn |
| EMAIL-01 | P05 | Verify đúng một lần; expired/wrong purpose/resend token cũ đều bị từ chối |
| EMAIL-02 | P05 | SMTP fail → outbox retry, user không mất/nhân đôi; expired OTP không gửi trễ |
| LOGIN-01 | P06 | Verified active + đúng password tạo session; disabled/unverified không tạo |
| JWT-01 | P06 | Invalid signature/alg/issuer/audience/expiry/nbf/missing required claims → 401 |
| ME-01 | P06 | GetMe chỉ actor, DTO không có hash/token digest/internal security fields |
| REF-01 | P07 | Refresh R1 tạo R2; DB không chứa plaintext; access claim/session đúng |
| REF-02 | P07 | R1 dùng lại revoke family; R2 và access cùng sid không dùng được sau commit |
| REF-03 | P07 | Hai request R1 cùng lúc: không có hai successor active; strict policy revoke family |
| REF-04 | P07 | Rotation vs logout đồng thời không để session/token active sau logout commit |
| REF-05 | P07 | Expired/unknown/missing cookie không tạo session; absolute expiry không kéo dài |
| OUT-01 | P07 | Logout 204 kể cả gọi lặp; clear cookie đúng Path/flags; access cũ → 401 |
| OUT-02 | P07 | Logout-all/revoke session chỉ đúng chủ; user khác vẫn dùng được |
| WEB-01 | P07 | Refresh/logout cross-origin thiếu/sai CSRF bị chặn; đúng origin+CSRF hoạt động |
| WEB-02 | P07 | Token response no-store; cookie HttpOnly/Secure/SameSite; token không trong URL |
| PWD-01 | P08 | Forgot unknown/known trả giống nhau; reset expired/replay/cross-purpose bị chặn |
| PWD-02 | P08 | Reset/change tăng version, revoke mọi session; không auto-login/disable MFA |
| PWD-03 | P08 | Parallel reset chỉ một consume; Google-only không tự thêm password qua forgot |
| MFA-01 | P09 | Login bật MFA chỉ challenge; pre-auth không được authenticate business API |
| MFA-02 | P09 | OTP đúng, sai, leading zero, expired, attempt limit, replay, cross-purpose |
| MFA-03 | P09 | Resend invalidates old code; không reset failed count/absolute deadline |
| MFA-04 | P09 | Concurrent verify chỉ một session; disable/enroll/regenerate require step-up |
| MFA-05 | P09 | Recovery code one-use; regenerate vô hiệu bộ cũ; reset password không bypass |
| OAUTH-01 | P10 | Valid callback đăng ký/login theo provider+sub; return URL allowlist |
| OAUTH-02 | P10 | Invalid state/nonce/signature/aud/issuer, expired code/replay bị chặn |
| OAUTH-03 | P10 | Email trùng không auto-link; identity đã gắn user khác không chuyển chủ |
| OAUTH-04 | P10 | Google login vẫn yêu cầu local MFA; completion ticket một lần và browser-bound |
| OAUTH-05 | P10 | Cancel/provider error không lộ raw secrets; unlink last method bị chặn |
| RBAC-01 | P11 | Anonymous/User/Admin matrix 401/403/success, ownership và mass assignment |
| RBAC-02 | P11 | Role/status change khiến JWT cũ mất quyền ngay sau commit |
| RBAC-03 | P11 | Last Admin không bị remove/disable; hai admin thay đổi song song vẫn giữ invariant |
| HIST-01 | P12 | History owner-only, paging/filter đúng, pending MFA không ghi login success |
| AUD-02 | P12 | MemoryAppender assert redaction/event fields; disk sink lỗi có retry/alert |
| AUD-03 | P12 | Worker restart/lease expiry có thể duplicate EventId nhưng không mất durable event |
| CLEAN-01 | P12 | Cleanup không xóa tombstone refresh khi family còn có thể replay |
| E2E-01 | P13 | Toàn flow local + MFA + rotation + logout hoàn tất; regression không skip |
| OPS-01 | P13 | Key rotation, SQL/SMTP outage, restore, limiter đa instance, proxy và TLS |

## 4. Test concurrency đúng cách

- Dùng hai DbContext/HTTP request độc lập và synchronization barrier, không test tuần tự giả song song.
- Assert số record active, consumed, session revoked và audit đã commit; không chỉ assert status HTTP.
- Test refresh-vs-refresh, refresh-vs-logout, OTP-vs-OTP, reset-vs-reset và last-admin mutation.
- Mỗi test DB/schema isolated; unique database name `AuthProject_Test_<random>`.
- Fixture chỉ được create/drop database có prefix test và đúng server allowlist;
  refuse nếu tên là `AuthProject` hoặc connection string từ production.
- Không chạy test phá dữ liệu trên connection string người dùng đưa cho ứng dụng.
- CI integration job cần SQL Server service/container thật; thiếu DB thì job bắt buộc fail,
  không silent skip rồi báo Green. Local có thể chỉ chạy unit và báo integration chưa chạy.

## 5. Lệnh từ P01

```powershell
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --no-build --logger trx
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --no-build --logger trx
dotnet test AuthProject.sln --no-build --collect:"XPlat Code Coverage"
```

Coverage để tìm nhánh chưa test, không coi % cao là bằng chứng bảo mật. Gate bắt buộc là
toàn bộ acceptance case của bước, đặc biệt deny/replay/race paths; không chạy coverage lặp khi không có thay đổi.
Mọi test skip có lý do ghi rõ; không mark Done nếu required test chưa chạy.

`Category=Unit` / `Category=Integration` phân loại suite; integration còn có `Dependency=None`
hoặc `Dependency=SqlServer`. Toàn integration suite cần Docker hoặc TestConnection được allowlist;
không đọc DefaultConnection. Shared helpers tại `TestSupport/` được link vào test assemblies,
không tạo project ứng dụng mới và không đưa fake vào API runtime.

## 6. Definition of Done của feature

- Có evidence Red/Green, case thành công + từ chối + boundary; race test nếu mutation cần atomic.
- API/DTO/error codes khớp Docs; no-secret logging và least-privilege được kiểm tra.
- EF migration/query/index được SQL integration test nếu có persistence change.
- Swagger, config example và runbook cập nhật; example không chứa secret thật.
- Build/unit/integration liên quan pass; báo rõ test manual hoặc environment còn thiếu.
- Không có business code/TODO thuộc bước kế tiếp được thêm mà chưa có yêu cầu.
