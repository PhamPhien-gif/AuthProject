# Test support (P01)

Source dùng chung được link vào hai project test; **không tạo thêm project/layer ứng dụng**
và không compile vào `AuthProject`. Không có implementation Auth production ở đây.

- `DatabaseTarget`: bắt buộc server allowlist + database `AuthProject_Test_...`, từ chối
  attach file/failover/user instance. Tạo catalog GUID mới, không dùng lại DB trong template.
- `FakeEmailTransport`: mailbox in-memory thread-safe, mô phỏng lỗi/cancellation, không mở SMTP.
- `FakeGoogleBackchannel`: HttpMessageHandler tại domain `.test`, discovery/JWKS và code exchange,
  ID token RS256 ký bằng key sinh trong memory, nonce/subject/email/fake time, code một lần 60 giây.
  Request ngoài endpoint đã định nghĩa bị lỗi ngay, không fallback network.
- `TestIdentityBuilder`: data builder dùng email `example.test` và provider subject riêng từng test.
- Clock dùng `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, host đăng ký dưới `TimeProvider`.

Fake Google chỉ mô phỏng **backchannel**, không trình duyệt/consent, state/correlation callback,
client secret hay PKCE. P10 sẽ bổ sung các ca protocol đó với middleware OIDC thật;
test P01 không chứng minh OAuth login đã an toàn/hoàn tất. P05/P10 nối các fake vào interface
production khi interface được tạo, không thêm interface nghiệp vụ trước vào P01.

`AuthWebApplicationFactory` cung cấp `Clock`, `Email`, `Google` cho integration tests.
Mỗi factory có state riêng; truyền `SqlServerFixture` đã initialize khi cần test DB.
Factory mặc định xóa DefaultConnection khỏi cấu hình để không kế thừa DB ứng dụng.
