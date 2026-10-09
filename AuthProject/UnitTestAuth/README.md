# UnitTestAuth

Project xUnit tham chiếu `AuthProject`, kiểm thử entity invariants, services, DTO validation
và security qua các folder trong project ứng dụng. Chưa có test nghiệp vụ ở bước P00.
Test rỗng từ template đã được bỏ để không báo thành công giả.

Khi bắt đầu từng bước: viết test hành vi thất bại trước, triển khai tối thiểu,
sau đó refactor. Dùng tên `Method_WhenCondition_ExpectedResult`, `[Theory]` cho
biên input, fake `TimeProvider` cho thời gian; không gọi SQL/SMTP/Google thật
trong unit test.

Integration test HTTP, SQL Server, concurrency sẽ nằm ở project riêng
`AuthProject.IntegrationTests` được tạo tại P01.

Xem [chiến lược TDD](../Docs/04-TDD-AND-ACCEPTANCE.md).
