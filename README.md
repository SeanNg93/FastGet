# FastGet Download Manager

Trình quản lý tải file cho Windows, mô phỏng cách hoạt động của Internet Download Manager (IDM).
Viết lại từ đầu (clean-room) bằng C# / .NET 10 WinForms, không chứa code, icon hay thương hiệu của IDM.
Hành vi cần mô phỏng được xác định bằng cách phân tích bản IDM đã cài trên máy bằng REA, xem
[docs/REA-findings.md](docs/REA-findings.md).

## Tính năng

| Tính năng | Ghi chú |
| --- | --- |
| Tải nhiều kết nối (mặc định 8) | Chia file thành nhiều đoạn theo HTTP Range; kết nối nào xong trước sẽ lấy một nửa đoạn lớn nhất còn lại (dynamic segmentation) |
| Tạm dừng / tiếp tục | Tiến độ từng đoạn lưu vào `%APPDATA%\FastGet\downloads.json`, tải tiếp được cả sau khi tắt app |
| Tự thử lại | Mỗi kết nối thử lại tối đa N lần; lỗi 4xx (trừ 408/429) dừng ngay |
| Category | Compressed / Documents / Music / Programs / Video / General, tự xếp theo đuôi file, mỗi loại một thư mục |
| Queue | "Download Later" đưa vào Main download queue, giới hạn số file tải cùng lúc |
| Giới hạn tốc độ | Token bucket dùng chung cho mọi kết nối |
| Batch download | `http://site/img*.jpg` với dải số hoặc chữ, có padding |
| Theo dõi clipboard | Copy một link có đuôi file nằm trong danh sách là hiện hộp thoại tải |
| Tích hợp trình duyệt | Extension MV3 cho Chrome / Edge / Brave: bắt lượt tải, menu chuột phải "Download with FastGet", "Download all links" |
| Khay hệ thống | Đóng cửa sổ thì app vẫn chạy dưới khay; mở lại bằng double-click |
| Import / Export | Danh sách URL dạng file text |

## Build và chạy

Yêu cầu .NET SDK 10.

```powershell
cd D:\Code\IDM-clone
dotnet build
dotnet run --project src\FastGet.App
dotnet test                                   # 6 test engine với HTTP server giả lập
dotnet publish src\FastGet.App -c Release -o publish   # bản chạy độc lập trong .\publish
```

## Cài tích hợp trình duyệt

1. Mở FastGet, vào **Downloads → Options → General → Browser integration**, bấm **Install**.
   App ghi native messaging host `com.fastget.host` vào HKCU cho Chrome, Edge, Brave và Chromium.
   Thao tác này không cần quyền admin.
2. Mở `chrome://extensions` (hoặc `edge://extensions`), bật **Developer mode**, bấm **Load unpacked**,
   chọn thư mục `extension` nằm cạnh `FastGet.exe` (nút **Open extension folder** sẽ mở thư mục này).
3. Extension có ID cố định `nomjcihllkmhgcflkembbleomaknglla` nhờ trường `key` trong manifest,
   khớp với `allowed_origins` mà app đăng ký.

Bấm vào icon extension để bật / tắt bắt lượt tải (khi tắt sẽ hiện badge `OFF`).
Nếu app không phản hồi, lượt tải sẽ được trả lại cho trình duyệt.

## Cấu trúc

```
src/FastGet.Core/        engine, không phụ thuộc UI
  DownloadTask.cs        tải nhiều kết nối, chia đoạn động, retry, ghi file theo offset
  DownloadManager.cs     danh sách, queue, đo tốc độ, lưu trạng thái
  Http.cs                probe (Range 0-0), đặt tên file, giới hạn tốc độ
  Settings.cs, Models.cs
src/FastGet.App/         WinForms
  Program.cs             single instance, chế độ native host
  Ipc.cs                 named pipe, native messaging, đăng ký registry
  UI/                    MainForm, Download File Info, Progress, Options, Batch, Links...
extension/               Chrome MV3 extension
tests/FastGet.Core.Tests xUnit + HTTP server tối giản qua socket
```

## Khác biệt so với IDM

Bản này cố ý chưa làm những phần sau: bắt video stream (HLS/DASH), site grabber, scheduler theo giờ,
proxy / xác thực, Dial-up, tự tắt máy khi xong queue, bắt lượt tải trong Firefox, và
nhiều queue. Riêng phần bắt lượt tải trong trình duyệt, IDM dùng thêm DLL hook mạng
(`IDMNetMon`, `IDMIECC`); bản này chỉ dùng extension và native messaging.
