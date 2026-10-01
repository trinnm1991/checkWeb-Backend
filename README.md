# CheckWeb Backend — server B

API/worker .NET 10 độc lập, không dashboard, không worker/API WAN. Chromium chụp website, kiểm tra SSL; PostgreSQL trên D lưu cấu hình/trạng thái. Ảnh và catalog builder ở B. Namespace C# cũ giữ nguyên để giảm thay đổi; executable mới là `CheckWeb.Backend.dll`.

## Build / chạy

```bash
dotnet restore CheckWeb.Backend.csproj
dotnet publish CheckWeb.Backend.csproj -c Release -o /tmp/checkweb-release
```

Dùng output mới; không publish chồng lên bản cũ. Runtime cần ASP.NET Core 10. Cấu hình `appsettings.json` được theo dõi Git chỉ chứa mặc định không secret; website seed mặc định **disabled** để tránh tự chụp lúc setup. Database mới được tạo bảng qua EnsureCreated; database hiện có không bị seed ghi đè. Chưa có migrations. Sau nghiệm thu bật website qua API/UI. `CaptureOnStartup=false` không dừng lịch của website đã enabled trong DB.

Copy `deploy/checkweb.env.example` sang `/etc/checkweb/checkweb.env` (root:root, 600), thay IP B/D và mật khẩu. Tạo role/database `visualmonitor` riêng trên D, cấp quyền tạo/đọc/ghi schema. Không dùng DB `wan_speed`. Chỉ B được truy cập role tương ứng, không mở PostgreSQL ra Internet.

Tạo user hệ thống `visualmon`; tạo `/opt/checkweb/publish`, `/opt/checkweb/browsers`, `/var/lib/checkweb/captures`, `/var/lib/checkweb/site-builder`, cấp quyền đọc binary và ghi dữ liệu/browser phù hợp. Chép publish vào thư mục runtime. Cài Chromium khớp bản publish:

```bash
cd /opt/checkweb/publish
pwsh ./playwright.ps1 install-deps chromium
runuser -u visualmon -- env PLAYWRIGHT_BROWSERS_PATH=/opt/checkweb/browsers pwsh ./playwright.ps1 install chromium
```

Cài `deploy/checkweb.service`, kiểm tra đường dotnet rồi daemon-reload và enable/start theo cửa sổ triển khai. Chỉ chạy một worker cho cùng DB/kho ảnh. Không kết nối DB production để thử nghiệm nếu worker cũ đang chạy.

## Swagger và đường dẫn

- `http://IP_B:5080/swagger/` — Swagger UI.
- `/swagger/v1/swagger.json` — OpenAPI.
- `/api/health`, `/api/sites`, `/api/monitor`, `/api/builder/sites` — API.
- `/captures/...` — tài nguyên ảnh, cần proxy từ A.
- `/`, `/index.html`, `/api/wan` — 404; B không phục vụ dashboard hoặc WAN.

Swagger mặc định bật, tắt bằng `Swagger__Enabled=false`. CheckWeb chưa có xác thực ứng dụng; giới hạn cổng B cho A và mạng quản trị. Swagger Try it out thực hiện thao tác thật, kể cả sửa/xóa/chụp, nên không công bố cổng này ẩn danh. Nginx A bảo vệ frontend/API/ảnh bằng HTTPS + Basic Auth hoặc SSO.

`Presets/*.json` nhúng vào DLL để seed builder, không public và không phụ thuộc frontend. Đồng bộ nội dung với preset A nếu chỉnh sửa; catalog đã lưu không bị tự ghi đè.

## Kiểm thử

Sau publish: `python3 tests/api-only.py /tmp/checkweb-release`. Test cần root/Linux/PostgreSQL 18 hoặc `PG_BIN`; dùng cluster/cổng/kho tạm, không chụp thật. Kiểm tra DB, preset, Swagger, endpoint ảnh và không phục vụ frontend/WAN. Không thay thế nghiệm thu Chromium, ba thiết bị và mạng thực trên B. Backup cả DB D, ảnh/catalog B và cấu hình bí mật.
