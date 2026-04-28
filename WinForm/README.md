# 🎬 Video Processor Pro

Tool xử lý video tự động: phụ đề, lồng tiếng, zoom, tốc độ — hoàn toàn **local & miễn phí**.

---

## ✨ Tính năng

| Tính năng | Công nghệ | Chi phí |
|---|---|---|
| Nhận dạng giọng nói → Phụ đề | OpenAI Whisper (local) | Miễn phí |
| Dịch phụ đề | Argos Translate (local) | Miễn phí |
| Lồng tiếng | Edge-TTS (Microsoft voices, online) | Miễn phí |
| Thêm sub vào video | FFmpeg subtitles filter | Miễn phí |
| Zoom video | FFmpeg crop+scale | - |
| Tăng tốc | FFmpeg setpts + atempo | - |
| Giảm âm gốc | FFmpeg volume filter | - |

### Chi tiết xử lý:
- 📝 **Phụ đề**: Nền đen, chữ trắng, căn giữa, bên dưới video
- 🔊 **Lồng tiếng**: Edge-TTS miễn phí (30+ giọng đọc, 12+ ngôn ngữ)
- 🔍 **Zoom**: 130% (crop center → scale, không méo)
- ⚡ **Tốc độ**: 1.3x (cả video + audio)
- 🔇 **Âm gốc**: -20dB (vẫn còn nghe nhưng nhỏ)
- 🎥 **Chất lượng**: CRF 18 (near lossless H.264)

---

## 🔧 Yêu cầu hệ thống

- **OS**: Windows 10/11 (x64)
- **RAM**: Tối thiểu 4GB (Whisper base model ~1GB)
- **Disk**: ~3GB (models + dependencies)
- **GPU**: Không bắt buộc (CPU chạy được, nhưng chậm hơn)
- **.NET**: 8.0 Runtime
- **Python**: 3.10+
- **FFmpeg**: Latest (thêm vào PATH)
- **Internet**: Chỉ cần khi Edge-TTS tạo giọng đọc (lần đầu tải model)

---

## 📦 Cài đặt

### Bước 1: Cài FFmpeg

```bat
winget install Gyan.FFmpeg
```
Hoặc tải thủ công: https://www.gyan.dev/ffmpeg/builds/
→ Giải nén và thêm `C:\ffmpeg\bin` vào PATH

### Bước 2: Cài Python 3.10+

https://www.python.org/downloads/
→ ✅ Check "Add Python to PATH"

### Bước 3: Cài .NET 8 Runtime

```bat
winget install Microsoft.DotNet.DesktopRuntime.8
```

### Bước 4: Build & Setup

```bat
cd VideoProcessor
Scripts\build.bat      # Build C# app
dist\setup_windows.bat # Cài Python packages
```

---

## 🚀 Sử dụng

1. Chạy `dist\VideoProcessor.exe`
2. Click **📂 Video** → chọn file MP4
3. Chọn thư mục output (hoặc để mặc định)
4. Cấu hình:
   - Ngôn ngữ gốc: `auto` (tự động detect)
   - Dịch sang: `vi` (tiếng Việt)
   - Bật/tắt từng tính năng
5. Click **▶ Bắt đầu xử lý**

---

## ⚙️ Cấu hình chi tiết

```
Volume gốc: -20 dB  (giảm âm gốc 20dB, lồng tiếng nổi bật hơn)
Zoom:        130%    (crop center, scale up)
Tốc độ:     1.3x    (video + audio đều nhanh hơn)
```

---

## 📁 Cấu trúc thư mục

```
VideoProcessor/
├── WinForm/
│   ├── MainForm.cs          # UI + Logic điều phối
│   ├── MainForm.Designer.cs
│   ├── Program.cs
│   └── VideoProcessor.csproj
├── Python/
│   ├── video_processor.py   # Pipeline: Whisper → Translate → TTS
│   └── setup.py             # Cài đặt dependencies
└── Scripts/
    ├── build.bat            # Build toàn bộ
    └── setup_windows.bat    # Kiểm tra & setup môi trường
```

---

## 🔄 Pipeline xử lý

```
Input MP4
    │
    ├─► [FFprobe] Đọc thông tin video (duration, resolution, fps)
    │
    ├─► [FFmpeg] Trích xuất audio WAV 16kHz mono
    │
    ├─► [Whisper] Nhận dạng giọng nói → segments JSON
    │
    ├─► [Argos Translate] Dịch text (local, offline)
    │
    ├─► [Edge-TTS] Tạo audio TTS cho từng segment
    │
    ├─► [FFmpeg] Merge TTS segments → dubbed audio track
    │
    └─► [FFmpeg] Final render:
            - Zoom (crop+scale)
            - Speed (setpts + atempo)
            - Subtitles (hardburn vào video)
            - Mix audio (gốc -20dB + lồng tiếng)
            - Encode H.264 CRF 18
            
Output: *_processed.mp4
```

---

## 🗣️ Giọng đọc Edge-TTS

| Ngôn ngữ | Voice |
|---|---|
| 🇻🇳 Tiếng Việt | vi-VN-NamMinhNeural |
| 🇺🇸 English | en-US-GuyNeural |
| 🇨🇳 Chinese | zh-CN-YunxiNeural |
| 🇯🇵 Japanese | ja-JP-KeitaNeural |
| 🇰🇷 Korean | ko-KR-InJoonNeural |
| 🇫🇷 French | fr-FR-HenriNeural |
| 🇩🇪 German | de-DE-ConradNeural |
| 🇪🇸 Spanish | es-ES-AlvaroNeural |

---

## ❓ FAQ

**Q: Lần đầu chạy lâu không?**
A: Có. Whisper cần tải model ~145MB, Argos cần tải language pack. Từ lần 2 trở đi nhanh hơn nhiều.

**Q: Không có internet có dùng được không?**
A: Whisper và Argos Translate chạy hoàn toàn offline sau khi đã tải model. Chỉ Edge-TTS cần internet.

**Q: GPU có tăng tốc không?**
A: Có! Whisper tự động dùng CUDA nếu có GPU NVIDIA. Cài `torch` với CUDA để tăng tốc.

**Q: Có thể thay đổi giọng TTS không?**
A: Sửa hàm `get_voice_for_language()` trong `video_processor.py`. Xem danh sách giọng: `edge-tts --list-voices`

**Q: Phụ đề không đúng thời gian?**
A: Điều chỉnh Whisper model từ `base` → `small` hoặc `medium` để chính xác hơn (chậm hơn).

---

## 📝 License

Tool này sử dụng các thư viện open-source:
- [OpenAI Whisper](https://github.com/openai/whisper) — MIT
- [Argos Translate](https://github.com/argosopentech/argos-translate) — MIT  
- [Edge-TTS](https://github.com/rany2/edge-tts) — GPL-3.0
- [FFmpeg](https://ffmpeg.org/) — LGPL/GPL
