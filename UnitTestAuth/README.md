# UnitTestAuth

Project xUnit tham chiếu `AuthProject`, dự kiến kiểm thử entity invariants, services, DTO validation
và security qua các folder trong project ứng dụng. P01 có 14 test cho DatabaseTarget và fake transports;
chưa có test nghiệp vụ Auth. Test rỗng từ template đã được bỏ.

Khi bắt đầu từng bước: viết test hành vi thất bại trước, triển khai tối thiểu,
sau đó refactor. Dùng tên `Method_WhenCondition_ExpectedResult`, `[Theory]` cho
biên input, fake `TimeProvider` cho thời gian; không gọi SQL/SMTP/Google thật
trong unit test.

Integration test HTTP và SQL Server nằm ở project riêng `AuthProject.IntegrationTests`, đã tạo tại P01.
Shared helpers được link từ `TestSupport/`; trait của project này là `Category=Unit`.

```powershell
dotnet test UnitTestAuth/UnitTestAuth.csproj --filter 'Category=Unit'
```

Xem [chiến lược TDD](../Docs/04-TDD-AND-ACCEPTANCE.md).
