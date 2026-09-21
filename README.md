# MCP SQL Server

Công cụ kết nối và kiểm tra Microsoft SQL Server nhanh chóng, an toàn, hỗ trợ song song 2 chế độ: **CLI Menu** (cho người dùng thao tác trực tiếp) và **MCP Server** (cho các trợ lý AI như Codex, Antigravity, Claude Desktop).

---

## 1. Repo này làm gì?

Repo này cung cấp giải pháp kết nối đến SQL Server qua giao thức ADO.NET thuần (.NET 10), đảm bảo:
- **Tối giản & Hiệu năng cao (Ponytail)**: Không dùng ORM cồng kềnh, truy vấn catalog trực tiếp.
- **Bảo mật tuyệt đối**: Không lưu mật khẩu ra đĩa, tự động ẩn mật khẩu (`*`) khi nhập trên terminal, che giấu mật khẩu trong toàn bộ log.
- **Run Gate**: Tự động chạy và yêu cầu vượt qua 100% unit tests trước khi khởi động ứng dụng.
- **100% Đường dẫn tương đối**: Tương thích tốt khi clone và chạy trên mọi máy tính.

---

## 2. Repo này làm được gì?

1. **CLI Menu tương tác**:
   - Nhập thông tin kết nối (Server name, Username, Password ẩn).
   - Tùy chỉnh nâng cao: Cổng Port, Database khởi đầu, Encrypt TLS, Bỏ qua chứng chỉ (`TrustServerCertificate` khi dùng Self-Signed Cert).
   - Kiểm tra kết nối tức thời, đo độ trễ mạng (ms) và lấy phiên bản SQL Server.
   - Đếm và hiển thị bảng danh sách database mà tài khoản nhìn thấy (phân loại Database Hệ thống vs Database Nghiệp vụ, trạng thái ONLINE/OFFLINE, quyền truy cập).
   - Đếm và liệt kê danh sách Table trong một database cụ thể (kèm Schema: `dbo`, `sales`, v.v.).
2. **MCP STDIO Server**:
   - Cung cấp 3 công cụ (tools) cho AI: `test_connection`, `list_databases`, và `list_tables`.
   - Chuẩn STDIO: Log đẩy sang `stderr`, dữ liệu JSON-RPC chuẩn đẩy sang `stdout`.

---

## 3. Hướng dẫn sử dụng cơ bản

### Yêu cầu
Máy tính đã cài đặt **.NET 10** trở lên (`dotnet --version`).

---

### Cách 1: Chạy Menu CLI trong Terminal

Mở Terminal (trong VS Code nhấn `Ctrl + \``) và chạy:

```pwsh
dotnet run --project ./src/McpSqlServer -- menu
```

*(Hoặc chạy qua script tự động kiểm tra test gate: `powershell -File ./scripts/run.ps1 menu`)*

**Thao tác trong Menu:**
- Gõ **`1`** + Enter: Nhập `Server`, `Username`, `Password` (có thể chọn lưu vào `dbconfig.json`).
- Gõ **`2`** + Enter: Kiểm tra kết nối.
- Gõ **`3`** + Enter: Xem danh sách và số lượng database.
- Gõ **`4`** + Enter: Đếm và xem danh sách các table trong một database.
- Gõ **`0`** + Enter: Thoát.

> [!TIP]
> **Tự động nạp thông tin kết nối từ file `dbconfig.json`:**
> Bạn có thể tạo file `dbconfig.json` (từ file mẫu [`dbconfig.example.json`](file:///d:/Visual%20Studio%20Code/mcp-sqlserver/dbconfig.example.json)) tại thư mục gốc:
> ```json
> {
>   "Server": "sql.example.internal",
>   "Username": "reader",
>   "Password": "YourPassword123",
>   "Database": "master",
>   "Port": 1433,
>   "Encrypt": true,
>   "TrustServerCertificate": false
> }
> ```
> - Nếu file `dbconfig.json` tồn tại và có thông tin: Ứng dụng (cả CLI Menu và MCP Server) sẽ tự động nạp kết nối.
> - Nếu file chưa có hoặc các trường bị để trống (`null`/`""`): Ứng dụng sẽ yêu cầu bạn nhập từng thông tin như bình thường.
> - File `dbconfig.json` đã được cấu hình trong `.gitignore` để **tuyệt đối không bị đẩy lên GitHub**.

---

### Cách 2: Dùng làm MCP Server cho AI (Codex, Claude, Antigravity)

1. Cài đặt các biến môi trường kết nối:
   ```pwsh
   $env:MSSQL_SERVER = "sql.example.internal"
   $env:MSSQL_USERNAME = "your_username"
   $env:MSSQL_PASSWORD = "your_password"
   $env:MSSQL_TRUST_CERTIFICATE = "true"  # Bật nếu server dùng SSL tự ký
   ```

2. Cấu hình vào file config của AI Client:
   ```json
   {
     "mcpServers": {
       "sqlserver": {
         "command": "dotnet",
         "args": ["run", "--project", "./src/McpSqlServer", "--", "serve"],
         "env": {
           "MSSQL_SERVER": "sql.example.internal",
           "MSSQL_USERNAME": "your_username",
           "MSSQL_PASSWORD": "your_password",
           "MSSQL_TRUST_CERTIFICATE": "true"
         }
       }
     }
   }
   ```

---

### Cách 3: Chạy Unit Tests

```pwsh
dotnet test ./tests/McpSqlServer.Tests
```
