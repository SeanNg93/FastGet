# Kết quả phân tích IDM bằng REA

Đối tượng: Internet Download Manager cài tại `C:\Program Files (x86)\Internet Download Manager`
(extension Chrome "IDM Integration Module" 6.43.1). Ngày phân tích: 2026-10-14.

Phương pháp: chỉ phân tích tĩnh, không chạy và không decompile mã native của IDM.
Code của FastGet viết mới hoàn toàn; phần dưới đây chỉ ghi lại *hành vi và giao diện* quan sát được.

## Evidence

| Evidence ID | Thao tác | Đối tượng |
| --- | --- | --- |
| `ev_c2e06fd0a950290a469608840732481cf9b67de5681c12f054aa99cbade600c7` | `inspect_pe_resources` | `IDMan.exe` (cây resource PE) |
| `ev_f57cb99bcc8e1262b9ec73eaeba70f7f9bc9720a621f8fa8420c857d5f71b2ee` | `analyze_javascript_application` | `IDMGCExt.crx` đã giải nén |
| `ev_770cf89135d29ce48413f6ee0572e6ae9e733c917c45fc392026e0bce952dfb8` | `inspect_analysis_view` (summary) | như trên |

## Quan sát (observed)

**Resource của IDMan.exe**: 174 bitmap, 105 dialog, 48 icon, 37 string table, 2 menu.

- Menu chính (resource 129): `Tasks` (Add new download, Add batch download, Add batch download
  from clipboard, Run site grabber, Export/Import), `File` (Stop Download, Remove, Download Now,
  Redownload), `Downloads` (Pause All, Stop All, Delete All Completed, Scheduler, Start/Stop queue,
  Speed Limiter on/off, Options), `View` (Hide categories, Arrange files by..., Toolbar...), `Help`.
- Dialog 103 "Download progress": thanh tiến trình, `<< Hide details`, `Pause`, `Cancel`,
  một ListView và nhãn "Start positions and download progress by connections".
- Dialog 130 "Download File Info": URL, Category, Save As, Description, nút
  `Start Download` / `Download Later` / `Cancel`.
- Dialog 141 "Connection": "Default max. conn. number", ngoại lệ số kết nối theo server,
  "Download no more than ... MBytes every ... hours".
- Dialog 143 / 204: category gồm tên, danh sách đuôi file ("separated by space"),
  danh sách site, thư mục lưu riêng.
- Dialog 280 "Batch download": dùng wildcard `*`, thay bằng số hoặc chữ, From/To, "Wildcard size".
- Dialog 228 "Download All Links with IDM": danh sách có checkbox, "Hide HTML files", Check/Uncheck All.
- Dialog 298: xử lý link trùng ("Add the duplicate with a numbered file name" / overwrite / resume).
- Dialog 291/294: tự bắt các loại file theo danh sách đuôi, danh sách site/địa chỉ ngoại lệ.
- Dialog 306/307: scheduler, số file tải cùng lúc trong queue.

**Native messaging**: `IDMMsgHost.json` đăng ký host `com.tonec.idm` (stdio) cho 2 extension ID.

**Extension** (manifest MV3, service worker `background.js`, content script trên mọi trang):
- Quyền: `downloads`, `downloads.shelf`, `webRequest`, `declarativeNetRequest`, `cookies`,
  `contextMenus`, `nativeMessaging`, `scripting`, `tabs`, ...
- `background.js` gọi `connectNative("com.tonec.idm")` và gửi message dạng mảng có mã số
  (`[14,…]`, `[15,…]`, … `[19,…]`).
- API được gọi: `downloads.onCreated`, `downloads.onDeterminingFilename`, `downloads.cancel/erase/pause/resume`,
  `webRequest.onBeforeRequest/onBeforeSendHeaders/onHeadersReceived/onResponseStarted/onCompleted`,
  `cookies.getAll`, `contextMenus.*`, `scripting.executeScript`.

## Suy luận (inferred)

- Extension bắt lượt tải bằng cách chặn download của trình duyệt rồi chuyển URL, cookie và referer
  sang app qua native messaging. Các hook `webRequest` có lẽ dùng để nhận diện file theo header phản hồi
  và để bắt video.
- "Start positions ... by connections" cho thấy mỗi kết nối có vị trí bắt đầu riêng trong file,
  tức là tải song song theo dải byte.

## Giới hạn

- Phân tích ngữ nghĩa JS chỉ đạt một phần: worker hết heap khi phân tích `background.js` (đã bị minify).
  Danh sách API ở trên lấy từ quét text, không phải từ call graph đầy đủ.
- Không có Ghidra/IDA nên chưa phân tích `IDMan.exe` ở mức hàm. Thuật toán chia đoạn của IDM
  **không được quan sát**. Cơ chế dynamic segmentation trong FastGet là thiết kế riêng.
- CRX không phải định dạng REA mở trực tiếp được; file được giải nén thành ZIP trước khi phân tích.

## Quyết định thiết kế của FastGet (không suy ra từ IDM)

- Probe bằng `Range: bytes=0-0`; nếu trả về 206 thì coi là resume được.
- Mặc định 8 kết nối, mở lần lượt cách nhau 150 ms. Chỉ chia đoạn khi phần còn lại ≥ 512 KB.
- Ghi trực tiếp vào file `.fgpart` theo offset (`RandomAccess.Write`), xong thì đổi tên.
- Message native messaging là JSON có tên trường, không dùng mảng mã số như IDM.
