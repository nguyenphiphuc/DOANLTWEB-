# Slidify - Nền tảng Thiết kế Slide Trực tuyến

Slidify là một ứng dụng web mô phỏng trải nghiệm thiết kế slide giống như Canva, cho phép người dùng tạo, chỉnh sửa, chia sẻ và quản lý các bài thuyết trình một cách trực quan, mượt mà ngay trên trình duyệt.

## Tính năng nổi bật (Features)

* **Thiết kế Kéo-Thả Trực quan:** Trình soạn thảo slide mạnh mẽ dựa trên Canvas (Fabric.js), hỗ trợ kéo thả văn bản, hình ảnh, hình khối.
* **Quản lý Bài Thuyết trình:** Khởi tạo, lưu trữ, và tổ chức dự án chuyên nghiệp.
* **Thư viện Template Đa dạng:** Cung cấp sẵn hơn 50 mẫu slide đẹp mắt được phân loại theo nhiều chủ đề (Kinh doanh, Giáo dục, Marketing, v.v.).
* **Hỗ trợ Làm việc Nhóm:** Tính năng chia sẻ bài thuyết trình, cấp quyền Xem/Chỉnh sửa cho thành viên khác.
* **Import File PPTX:** Tính năng dành cho Admin để import trực tiếp file PowerPoint vào hệ thống và chuyển đổi thành dạng Canvas.
* **Thành viên VIP & Thanh toán:** Tích hợp hệ thống nâng cấp tài khoản VIP thông qua mã QR ngân hàng (SePay / VietQR) để mở khóa các tính năng và template cao cấp.
* **Giao diện Hiện đại (Dark Mode):** Hệ thống giao diện cực kỳ mượt mà, sử dụng các chuẩn thiết kế hiện đại nhất với hiệu ứng glassmorphism và gradient đẹp mắt.

## Công nghệ sử dụng (Tech Stack)

* **Backend:** ASP.NET Core MVC (.NET 10)
* **Database:** SQL Server kết hợp Entity Framework Core (Code-First)
* **Identity & Security:** ASP.NET Core Identity, xác thực JWT, chống tấn công CSRF.
* **Frontend:** HTML5, CSS3 (Vanilla + Bootstrap 5), JavaScript (ES6)
* **Canvas Engine:** Fabric.js (Tùy biến riêng cho Slidify)
* **Xử lý Ảnh/PDF:** SixLabors.ImageSharp, Docnet.Core

## Cài đặt và Chạy dự án (How to Run)

1. **Yêu cầu môi trường:**
   - .NET 10 SDK
   - SQL Server (LocalDB hoặc phiên bản đầy đủ)
   - Visual Studio 2022 hoặc VS Code.

2. **Các bước thực hiện:**
   - Clone repository về máy.
   - Mở Terminal/Command Prompt tại thư mục dự án.
   - Chạy lệnh `dotnet restore` để tải các thư viện NuGet.
   - Cập nhật chuỗi kết nối Database trong `appsettings.json` (nếu cần thiết). Mặc định đang sử dụng LocalDB.
   - Chạy `dotnet run` (hoặc nhấn F5 trong Visual Studio).
   - Truy cập `https://localhost:7272` trên trình duyệt.

3. **Tài khoản Admin Mặc định:**
   - Trong quá trình chạy lần đầu, `DbInitializer` sẽ tự động tạo tài khoản Admin.
   - Email: `admin@gmail.com`
   - Mật khẩu: `123`
   - *Lưu ý: Bạn nên đăng nhập bằng tài khoản này để duyệt Template và thao tác Import.*

## Các Bản Cập nhật Gần Nhất (Recent Updates)

- **Tối ưu hóa API Lưu Slide:** Cải thiện hiệu suất EF Core, chỉ thực hiện update và thêm mới slide thay vì xóa toàn bộ.
- **Bảo mật:** Bổ sung `[ValidateAntiForgeryToken]` cho toàn bộ hệ thống API POST (tránh lỗi bảo mật CSRF).
- **Làm gọn Code:** Loại bỏ các controller và model thừa không còn sử dụng.
- **Cấu hình Database:** Fix cảnh báo Warning cho định dạng tiền tệ `decimal(18,2)`.

---
*Dự án Đồ án Lập trình Web - Phát triển bởi Nhóm 4*
