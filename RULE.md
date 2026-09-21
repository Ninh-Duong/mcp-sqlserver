# Repository Governance & Engineering Rules

Tài liệu quy định các nguyên tắc bắt buộc khi phát triển, vận hành và đóng góp mã nguồn cho repository `mcp-sqlserver`.

---

## 1. Bảo mật & Zero-Leak Credentials
1. **Tuyệt đối không commit bí mật:** Không bao giờ đưa username, password, token, connection string thực tế hoặc file cấu hình nhạy cảm lên Git.
2. **Che giấu thông tin (Sanitization):** Mọi log lỗi, exception message và trace ghi ra stderr/file log phải tự động lọc bỏ hoặc thay thế mật khẩu bằng `***`.
3. **Mật khẩu CLI:** Nhập mật khẩu trên terminal bắt buộc phải che ký tự (`*`), chỉ lưu tạm thời trong bộ nhớ phiên làm việc, không lưu ra file plain text.

---

## 2. Quy tắc Đường dẫn Tương đối (100% Relative Paths)
1. **Cấm tuyệt đối hardcoded absolute path:** Không sử dụng các đường dẫn tuyệt đối chứa ổ đĩa như `C:\...`, `D:\...`, hoặc `/home/...` trong code, test, script, log file path hay config.
2. **Xác định đường dẫn chuẩn:** Mọi đường dẫn file/thư mục cục bộ (như thư mục log, publish, test assets) phải được tính tương đối từ working directory hoặc `AppContext.BaseDirectory`.

---

## 3. Quy tắc Run Gate & Unit Tests
1. **100% Process Features có Unit Test:** Mọi tính năng nghiệp vụ (Pre-flight check, ConnectionOptions builder/validation, SQL parser/counter, Logger sanitizer, CLI masking, MCP tool contracts) đều phải có unit test tương ứng.
2. **Bắt buộc Pass Test Mỗi Lần Khởi Chạy:**
   - Khi chạy qua script (`./scripts/run.ps1`), toàn bộ unit test được thực thi trước. Nếu có bất kỳ test nào thất bại, script lập tức dừng tiến trình.
   - Khi khởi động ứng dụng (kể cả standalone binary), hàm `PreflightChecker.RunCoreSelfTests()` sẽ chạy một bộ kiểm thử logic tự động in-process. Nếu fail, chương trình từ chối hoạt động.

---

## 4. Tách bạch chuẩn STDIO cho MCP
1. **Quy tắc luồng `stdout`:** Trong chế độ MCP (`mcp-sqlserver serve`), luồng `stdout` là tài nguyên độc quyền của giao thức JSON-RPC. Tuyệt đối không gọi `Console.WriteLine` hoặc xuất log trực tiếp ra `stdout`.
2. **Quy tắc luồng `stderr`:** Mọi log chẩn đoán, tiến trình kết nối và thông báo lỗi hệ thống bắt buộc đẩy sang `Console.Error` (`stderr`) hoặc ghi vào file log tương đối `./logs/process.log`.

---

## 5. Nguyên lý Ponytail (Tối giản & Hiệu năng)
1. **Không abstraction dư thừa:** Không tạo interface cho lớp chỉ có 1 implementation; không dùng Factory pattern nếu có thể khởi tạo trực tiếp; không dùng ORM nặng nề khi 1 câu SQL `sys.databases` đã giải quyết trọn vẹn bài toán.
2. **Cấu trúc hàm tối ưu:** Hàm ngắn gọn, đơn nhiệm, async xuyên suốt kèm `CancellationToken` cho mọi I/O và SQL query.
3. **Xóa bỏ hơn là thêm vào:** Giữ codebase gọn gàng, loại bỏ mọi code dự phòng "để dành cho tương lai".
