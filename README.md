# FastGet Download Manager

Trình quản lý tải file cho Windows, mô phỏng cách hoạt động của Internet Download Manager (IDM).
Viết lại từ đầu (clean-room) bằng C# / .NET 10 WinForms, không chứa code, icon hay thương hiệu của IDM.
Hành vi cần mô phỏng được xác định bằng cách phân tích bản IDM đã cài trên máy bằng REA, xem
[docs/REA-findings.md](docs/REA-findings.md).

## Tải về

Vào [Releases](https://github.com/SeanNg93/FastGet/releases/latest) và chọn một trong hai:

- `FastGet-<version>-setup.exe`: bộ cài cho Windows 10/11 x64. Cài theo từng user, không cần quyền admin.
  Có tuỳ chọn đăng ký tích hợp trình duyệt và tự chạy cùng Windows. Gỡ được trong *Settings → Apps*.
- `FastGet-<version>-win-x64-portable.zip`: bản portable, giải nén ra là chạy.

Cả hai đã đóng gói sẵn .NET nên không cần cài runtime. Bộ cài chưa được ký số, vì vậy Windows SmartScreen
có thể cảnh báo "Windows protected your PC"; bấm **More info → Run anyway** để tiếp tục.

## Tính năng

| Tính năng | Ghi chú |
| --- | --- |
| Tải nhiều kết nối (mặc định 8) | Chia file thành nhiều đoạn theo HTTP Range; kết nối nào xong trước sẽ lấy một nửa đoạn lớn nhất còn lại (dynamic segmentation) |
| Tạm dừng / tiếp tục | Tiến độ từng đoạn lưu vào `%APPDATA%\FastGet\downloads.json`, tải tiếp được cả sau khi tắt app |
| Tự thử lại | Mỗi kết nối thử lại tối đa N lần; lỗi 4xx (trừ 408/429) dừng ngay |
| **Bắt video stream** | Nút "Download this video" nổi trên video đang phát; chọn chất lượng từ HLS master playlist; tải các segment song song, giải mã AES-128, ghép thành `.ts` (hoặc `.mp4` với fMP4) |
| Category | Compressed / Documents / Music / Programs / Video / General, tự xếp theo đuôi file, mỗi loại một thư mục |
| Queue | "Download Later" đưa vào Main download queue, giới hạn số file tải cùng lúc, tự thử lại file lỗi |
| **Hẹn giờ (Scheduler)** | Bắt đầu queue lúc HH:mm, một lần hoặc hằng ngày theo thứ; dừng queue lúc HH:mm |
| **Khi queue xong** | Không làm gì / thoát FastGet / tắt máy / Sleep / Hibernate, có đếm ngược 30 giây để huỷ |
| Giới hạn tốc độ | Token bucket dùng chung cho mọi kết nối |
| Batch download | `http://site/img*.jpg` với dải số hoặc chữ, có padding |
| Theo dõi clipboard | Copy một link có đuôi file nằm trong danh sách là hiện hộp thoại tải |
| Tích hợp trình duyệt | Extension cho **Chrome / Edge / Brave** (MV3) và **Firefox** (MV2): bắt lượt tải, menu chuột phải "Download with FastGet", "Download all links", bắt video |
| Khay hệ thống | Đóng cửa sổ thì app vẫn chạy dưới khay; mở lại bằng double-click |
| Import / Export | Danh sách URL dạng file text |

## Build và chạy

Yêu cầu .NET SDK 10.

```powershell
dotnet build
dotnet run --project src\FastGet.App
dotnet test                                            # 13 test: engine, HLS, scheduler, queue
./build-release.ps1 -Version 1.1.0                     # exe tự đóng gói + zip portable + bộ cài (cần Inno Setup 6)
```

Push một tag `v*` (ví dụ `git tag v1.2.0 && git push origin v1.2.0`) thì GitHub Actions
(`.github/workflows/release.yml`) sẽ tự build, chạy test và tạo Release kèm bộ cài.

## Cài tích hợp trình duyệt

1. Mở FastGet, vào **Downloads → Options → General → Browser integration**, bấm **Install**.
   App ghi native messaging host `com.fastget.host` vào HKCU cho Chrome, Edge, Brave, Chromium và Firefox.
   Thao tác này không cần quyền admin.
2. **Chrome / Edge / Brave**: mở `chrome://extensions` (hoặc `edge://extensions`), bật **Developer mode**,
   bấm **Load unpacked**, chọn thư mục `extension\chrome` nằm cạnh `FastGet.exe`
   (nút **Open extension folder** sẽ mở thư mục `extension`).
   Extension có ID cố định `nomjcihllkmhgcflkembbleomaknglla` nhờ trường `key` trong manifest.
3. **Firefox**: mở `about:debugging` → **This Firefox** → **Load Temporary Add-on**, chọn
   `extension\firefox\manifest.json`. Add-on tạm sẽ mất khi tắt Firefox; muốn cài cố định cần
   ký file `.xpi` qua addons.mozilla.org (có thể chọn "unlisted"), hoặc dùng Firefox Developer Edition
   với `xpinstall.signatures.required = false`. ID add-on: `fastget@fastget.app`.

Bấm vào icon extension để bật / tắt bắt lượt tải (khi tắt sẽ hiện badge `OFF`).
Nếu app không phản hồi, lượt tải sẽ được trả lại cho trình duyệt.

### Bắt video

Mở trang có video và bấm phát. Extension ghi lại các phản hồi media của tab (theo `Content-Type`
và đuôi URL: HLS `.m3u8`, file video `mp4/webm/...`). Rê chuột lên video sẽ hiện nút
**Download this video**; bạn cũng có thể chuột phải vào video → **Download with FastGet**.
FastGet liệt kê các chất lượng tìm được, chất lượng cao nhất nằm đầu tiên.

Nếu có `ffmpeg.exe` trong `PATH`, file `.ts` sẽ được tự remux sang `.mp4`, không encode lại.

## Cấu trúc

```
src/FastGet.Core/        engine, không phụ thuộc UI
  DownloadTask.cs        tải nhiều kết nối, chia đoạn động, retry, ghi file theo offset
  Hls.cs                 đọc playlist HLS, tải segment song song, AES-128, ghép file
  DownloadManager.cs     danh sách, queue, đo tốc độ, lưu trạng thái
  Scheduler.cs           hẹn giờ bắt đầu / dừng queue
  Http.cs                probe (Range 0-0), đặt tên file, giới hạn tốc độ
  Settings.cs, Models.cs
src/FastGet.App/         WinForms
  Program.cs             single instance, chế độ native host
  Ipc.cs                 named pipe, native messaging, đăng ký registry
  UI/                    MainForm, Download File Info, Progress, Options, Batch, Links,
                         Video, Scheduler, đếm ngược tắt máy
extension/common/        background.js + content.js dùng chung
extension/chrome/        manifest MV3
extension/firefox/       manifest MV2
tests/FastGet.Core.Tests xUnit + HTTP server tối giản qua socket
```

## Giới hạn

- **DASH (`.mpd`)** chưa hỗ trợ, nên các trang chỉ phát DASH sẽ không hiện video để tải.
- **Video có DRM** (Widevine, PlayReady, FairPlay, SAMPLE-AES) không tải được. App sẽ báo rõ lỗi này.
- HLS có **audio tách riêng** (`#EXT-X-MEDIA TYPE=AUDIO`) sẽ được ghi chú "video only", vì ghép âm thanh vào cần ffmpeg.
- Với **livestream**, app chỉ tải các segment đang có trong playlist tại thời điểm bấm tải.
- Chưa làm: site grabber, proxy và xác thực, nhiều queue.
- Riêng phần bắt lượt tải trong trình duyệt, IDM dùng thêm DLL hook mạng (`IDMNetMon`, `IDMIECC`);
  bản này chỉ dùng extension và native messaging.

## License

MIT, xem [LICENSE](LICENSE).
