# Kết quả P05 — Register, Verify Email, Resend và SMTP outbox

Ngày: 09/10/2026. **Hoàn tất P05**, P06–P13 chờ lệnh. Kiến trúc vẫn một project API,
layer theo folder; hai project xUnit riêng để test.

## 1. Thay đổi và hành vi

- `Controllers/RegistrationController.cs`: POST `/api/v1/auth/register`, `/email/verify`, `/email/resend`;
  anonymous explicit, named rate policies P04, DTO validation/error pipeline P02, no-store.
- `Services/RegistrationService.cs`, `Services/Interfaces/IRegistrationService.cs`:
  validate cả khi gọi service trực tiếp, hash password, orchestration và lỗi catalog.
- `Repositories/RegistrationStore.cs`, interface: SQL/EF/LINQ, range/user locks và transaction.
  Email trim/normalize, displayName trim, password giữ nguyên. Register gán role User từ server;
  user/challenge/audit/email-outbox commit cùng nhau. Duplicate không ghi đè dữ liệu hoặc tạo thêm email.
- `Security/EmailVerificationToken.cs`: token 32 random bytes CSPRNG, canonical Base64URL 43 ký tự;
  challenge lưu SHA-256 digest 64 hex, không raw token. Purpose VerifyEmail, expiry/AuthVersion/one-use.
- `Services/Email/`: protected payload purpose riêng, template text, MailKit transport và dispatcher.
- `BackgroundJobs/VerificationEmailWorker.cs`: worker chỉ xử lý email verification, không xử lý audit outbox.
- `Configuration/RegistrationOptions.cs`, `RegistrationServices.cs`, Program/appsettings/example và Docs cập nhật.
  SMTP MailKit 4.17.0 pin ở central packages; lock files cả ba project cập nhật.

Register/Resend hợp lệ trả cùng 202 và message generic, không userId/challenge/token/mailbox,
kể cả duplicate hoặc unknown. Verify thành công 204; invalid/expired/replay/purpose/version/disabled
trả 400 AUTH_CHALLENGE_INVALID. JSON field ngoài DTO (ví dụ role Admin) bị reject.
Không có Login/JWT/GetMe/MFA hoặc session creation trong P05. Account phải được verify trước flow P06.

## 2. Persistence và concurrency

Register dùng `UPDLOCK,HOLDLOCK` theo normalized email để khóa cả missing-key range;
unique index P03 là invariant cuối. Unique race được rollback rồi kiểm tra competing user,
giữ public generic result; không nuốt lỗi constraint khi không có user cạnh tranh.
Duplicate cũng hash input hợp lệ trước lookup để giảm chênh lệch chi phí hashing, không hứa timing tuyệt đối.

Verify lookup digest đúng purpose, lock User rồi đọc challenge mới trong transaction.
Chỉ pending user và challenge chưa consume, còn hạn, đúng AuthVersion được activate.
Counterpart song song chờ lock và thấy consumed state; chỉ một verify thắng. Audit EmailVerified cùng commit.

Resend lock User, kiểm tra cooldown và số challenge đã phát trong rolling send window.
Default 60 giây cooldown + 3 sends/900 giây, tính cả Register; không reset budget khi thay token.
Sau quota check, invalidate toàn bộ verify challenge chưa consume và tạo challenge/outbox/audit mới atomic.
Unknown/verified/disabled hoặc quota cooldown bị chặn đều generic 202. Quota IP/account P04 độc lập.

P05 dùng schema P03/P04 có sẵn; thêm enum audit được lưu dạng string, **không cần migration mới**.
Không update database `AuthProject`. Nếu chưa apply P04, chạy lệnh update theo Docs/05 trước dùng flow.

## 3. Gửi email an toàn sau commit

Protected payload chứa recipient/token/expiry/challenge/user ID, mã hóa bằng Data Protection purpose
`AuthProject.VerificationEmail.v1`, tách khỏi audit. Cần giữ keys bền vững để retry sau restart.
SQL challenge/audit không có raw token/password; audit không có email body hoặc SMTP secret.

Template dùng HTTPS verification URL cấu hình cố định, token trong fragment `#token=...`;
không lấy Host header của request. Client cần đọc fragment và POST JSON token để verify.
Backend chưa có frontend; có thể lấy token trong email để verify bằng Swagger khi phát triển.
Không có GET tự xác minh và không gửi JWT trong email.

MailKit yêu cầu SSL-on-connect port 465 hoặc mandatory STARTTLS trên port khác, authenticate SMTP,
certificate validation mặc định. Không protocol logger/cert bypass/plaintext fallback.
Stable Message-ID từ outbox ID, gửi có timeout 30 giây, không log raw SMTP exception/body/recipient/token.
Tham khảo [MailKit package](https://www.nuget.org/packages/MailKit/4.17.0) và
[mandatory STARTTLS behavior](https://mimekit.net/docs/html/M_MailKit_Net_Smtp_SmtpClient_ConnectAsync_1.htm).

Dispatcher conditional claim bằng rowversion, commit lease trong transaction ngắn;
capture rowversion khi khóa claim vẫn còn giữ, completion/retry chỉ update khi còn sở hữu claim.
Lease 120 giây, restart reclaim khi hết hạn. Trước gửi kiểm tra user/challenge/digest/purpose/version/expiry
và current lease; email replaced/consumed/expired/disabled xử lý terminal, không gửi.
Giữ user lock trong send để serialize với Verify/Resend; tradeoff SQL connection/lock cần đo ở P13.

SMTP lỗi không rollback user đã commit; retry exponential 30–900 giây. Worker poll 5 giây, tối đa
10 message/lượt; tối đa 10 attempts, record thất bại vẫn pending để operator điều tra/requeue đúng ID.
Worker SQL outage chỉ log loại exception và retry vòng sau. Monitoring/dead-letter UI/retention/cleanup
và delivery audit log4net chưa triển khai, thuộc P12/P13.

Delivery at-least-once: crash sau SMTP accept trước SQL processed acknowledgment có thể gửi lặp.
Message-ID không bảo đảm provider deduplicate; recipient click lại cùng token vẫn one-use.
ProcessedAt nghĩa đã xử lý terminal, bao gồm skip stale email, không khẳng định delivery vào inbox.

## 4. Bằng chứng TDD và kiểm chứng

Red: 6 token tests chạy trước implementation, fail với NotImplementedException.
Green: random canonical token/digest implementation làm cả 6 pass.
Red SQL: `Register_CommitsPendingUserDefaultRoleAndEmailOutbox` chạy trên SQL Express test riêng,
fail tại RegistrationService stub với NotImplementedException. Green: service/store/transaction/outbox
làm test pass. Sau đó mở rộng concurrency/failure/HTTP/config regression; không gọi mọi case là Red trước.
Lỗi NuGet sandbox TLS/compiler không được tính là Red nghiệp vụ.

| Suite Release | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit | 77 | 0 | 0 |
| HTTP/startup không cần SQL | 81 | 0 | 0 |
| SQL integration, gồm HTTP flow với SQL | 38 | 0 | 0 |
| Tổng | **196** | **0** | **0** |

Thêm 41 test so với P04 155: 9 unit + 13 HTTP/startup + 19 SQL-dependent.
Unit: entropy/encoding/digest, payload roundtrip/privacy/purpose/tamper, template fragment/expiry.
HTTP: disabled 503 không truy cập DB, Swagger ba operation, startup config an toàn; flow real endpoint
register duplicate/resend unknown/verify replay với SQL, input/password/role injection.
SQL: 6 register đồng thời chỉ một user/role/challenge/audit; 2 verify đồng thời một winner,
expiry/purpose/version/disabled, resend cooldown/budget/new window/token cũ, email protection rollback,
SMTP failure/backoff/same message retry, stale mail skip, claim concurrency, abandoned lease và max attempts.

Restore locked-mode thành công; build Release 0 warning/error; NuGet dependency audit direct/transitive
không thấy advisory từ nguồn đang dùng tại thời điểm kiểm tra. Không diễn giải audit này là chứng minh tuyệt đối an toàn.

```powershell
dotnet restore AuthProject.sln --locked-mode
dotnet build AuthProject.sln --configuration Release --no-restore
dotnet test UnitTestAuth/UnitTestAuth.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P05/Unit
dotnet test AuthProject.IntegrationTests/AuthProject.IntegrationTests.csproj --configuration Release --no-build --logger trx --results-directory TestResults/P05/Integration
dotnet list AuthProject.sln package --include-transitive --vulnerable --no-restore
```

SQL dùng `ConnectionStrings__TestConnection` + server allowlist, database `AuthProject_Test_<GUID>`
tạo/drop riêng; không dùng DefaultConnection. Reports `TestResults/P05/` được ignore.
HTTP SQL tests remove email worker để không gửi SMTP thật; dispatcher tests dùng fake transport.
Docker/GitHub Actions, SMTP provider/inbox thật chưa chạy trong phiên này.

## 5. Cấu hình và bước tiếp theo

Registration mặc định tắt. Cần bật `Persistence__Enabled`, `Auth__Email__Enabled`,
`Auth__Registration__Enabled`; cung cấp SMTP credentials, Data Protection keys và HTTPS VerificationUrl.
Thiếu/sai config fail startup; module tắt trả safe 503. Không sửa `.env` riêng của bạn, chỉ bổ sung
[.env.example](../.env.example) và [hướng dẫn local](05-LOCAL-DEVELOPMENT.md#11-bật-registerverifyresend-p05).

P06 — Login/JWT/authentication/GetMe **chờ lệnh**.
