#!/usr/bin/env python3
import sys
import os
import json
import asyncio
import argparse
import subprocess

# Fix Windows encoding
if sys.platform == "win32":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')


def log(msg, level="INFO"):
    print(f"[{level}] {msg}", flush=True)


# ─────────────────────────────────────────────
# 1. TRANSCRIBE
# ─────────────────────────────────────────────
def transcribe_audio(audio_path, language=None):
    try:
        import whisper
    except ImportError:
        subprocess.run([sys.executable, "-m", "pip", "install", "openai-whisper", "-q"], check=True)
        import whisper

    model = whisper.load_model("base")

    result = model.transcribe(audio_path, language=language)

    segments = []
    for seg in result["segments"]:
        segments.append({
            "start": seg["start"],
            "end": seg["end"],
            "text": seg["text"].strip()
        })

    return segments, result.get("language", "en")


# ─────────────────────────────────────────────
# 2. TRANSLATE (FIXED)
# ─────────────────────────────────────────────
def ensure_model(src, tgt):
    import argostranslate.package

    argostranslate.package.update_package_index()
    available = argostranslate.package.get_available_packages()

    pkg = next((p for p in available if p.from_code == src and p.to_code == tgt), None)

    if not pkg:
        log(f"No model for {src}->{tgt}", "WARN")
        return

    installed = argostranslate.package.get_installed_packages()
    installed_pairs = [(p.from_code, p.to_code) for p in installed]

    if (src, tgt) not in installed_pairs:
        log(f"Installing model {src}->{tgt}")
        argostranslate.package.install_from_path(pkg.download())

def translate_segments(segments, src_lang, tgt_lang):
    import argostranslate.translate

    # normalize
    if src_lang.lower().startswith("zh"):
        src_lang = "zh"

    log(f"Translate: {src_lang} -> {tgt_lang}")

    # Chỉ tải mô hình dịch nếu ngôn ngữ đích khác ngôn ngữ nguồn
    if src_lang != tgt_lang:
        if src_lang != "en":
            ensure_model(src_lang, "en")
        if tgt_lang != "en":
            ensure_model("en", tgt_lang)

    translated = []

    for seg in segments:
        text = seg["text"].strip()

        if not text:
            translated.append({**seg, "text": ""})
            continue

        try:
            # Xử lý thông minh các luồng dịch
            if src_lang == tgt_lang:
                final_text = text
            elif src_lang == "en":
                # Nguồn là tiếng Anh -> Dịch thẳng sang ngôn ngữ đích
                final_text = argostranslate.translate.translate(text, "en", tgt_lang)
            elif tgt_lang == "en":
                # Đích là tiếng Anh -> Dịch thẳng từ ngôn ngữ nguồn
                final_text = argostranslate.translate.translate(text, src_lang, "en")
            else:
                # Nguồn và đích đều không phải tiếng Anh -> Bắt cầu qua tiếng Anh
                en_text = argostranslate.translate.translate(text, src_lang, "en")
                final_text = argostranslate.translate.translate(en_text, "en", tgt_lang)

            log(f"{text} -> {final_text}", "DEBUG")

        except Exception as e:
            log(f"❌ ERROR: {e}", "WARN")
            final_text = text  # Fallback lại văn bản gốc nếu lỗi để không bị crash

        translated.append({
            **seg,
            "text_src": text,
            "text": final_text
        })

    return translated

# ─────────────────────────────────────────────
# 3. TTS
# ─────────────────────────────────────────────
# ─────────────────────────────────────────────
# 3. TTS (ĐÃ TỐI ƯU HÓA)
# ─────────────────────────────────────────────
def get_voice_for_language(lang):
    return {
        "vi": "vi-VN-NamMinhNeural",       # Vietnamese male
        "en": "en-US-GuyNeural",           # English male
        "zh": "zh-CN-YunxiNeural",         # Chinese
        "ja": "ja-JP-KeitaNeural",         # Japanese
        "ko": "ko-KR-InJoonNeural",        # Korean
        "fr": "fr-FR-HenriNeural",         # French
        "de": "de-DE-ConradNeural",        # German
        "es": "es-ES-AlvaroNeural",        # Spanish
        "ru": "ru-RU-DmitryNeural",        # Russian
        "th": "th-TH-NiwatNeural",         # Thai
        "id": "id-ID-ArdiNeural",          # Indonesian
        "pt": "pt-BR-AntonioNeural",       # Portuguese
    }.get(lang, "en-US-GuyNeural")


async def process_all_tts(segments, voice, output_dir):
    import edge_tts
    import re
    import asyncio
    import os
    
    result = []

    for i, seg in enumerate(segments):
        text = seg["text"].strip()

        # Lọc bỏ ký tự chết
        text_clean = re.sub(r'[^\w\s]', '', text)
        if not text_clean.strip():
            result.append({**seg, "audio_path": None})
            continue

        path = os.path.join(output_dir, f"seg_{i:04d}.mp3")
        
        # === CƠ CHẾ TỰ ĐỘNG THỬ LẠI (AUTO-RETRY) ===
        max_retries = 3
        success = False
        
        for attempt in range(max_retries):
            try:
                # Lần 1 & 2 chạy rate +25%. Nếu vẫn lỗi, lần 3 trả về tốc độ gốc để tránh bị server bắt lỗi parameter.
                current_rate = "+25%" if attempt < 2 else "+0%"
                
                communicate = edge_tts.Communicate(text, voice, rate=current_rate)
                await communicate.save(path)
                
                # Kiểm tra chắc chắn file đã tải về và có dung lượng > 0
                if os.path.exists(path) and os.path.getsize(path) > 0:
                    result.append({**seg, "audio_path": path})
                    success = True
                    break  # Thành công thì thoát vòng lặp thử lại
                else:
                    raise Exception("File trống (No audio received)")
                    
            except Exception as e:
                if attempt < max_retries - 1:
                    # Nếu lỗi, chờ 2 giây để server nhả block rồi mới thử lại
                    await asyncio.sleep(2)
                else:
                    log(f"❌ Bó tay sau 3 lần thử ở đoạn [{text}]: {e}", "WARN")
        
        if not success:
            result.append({**seg, "audio_path": None})
            
        # Nghỉ ngơi 0.3 giây giữa các câu bình thường để tránh spam server
        await asyncio.sleep(0.3)

    return result


def generate_tts_audio(segments, tgt_lang, output_dir):
    import asyncio
    os.makedirs(output_dir, exist_ok=True)
    voice = get_voice_for_language(tgt_lang)
    
    # Chạy 1 luồng duy nhất cho toàn bộ danh sách, tránh nghẽn mạng
    return asyncio.run(process_all_tts(segments, voice, output_dir))


# ─────────────────────────────────────────────
# 4. SRT
# ─────────────────────────────────────────────
def format_srt_time(s):
    h = int(s // 3600)
    m = int((s % 3600) // 60)
    sec = int(s % 60)
    ms = int((s - int(s)) * 1000)
    return f"{h:02}:{m:02}:{sec:02},{ms:03}"


def write_srt(segments, path):
    with open(path, "w", encoding="utf-8") as f:
        for i, seg in enumerate(segments, 1):
            text = seg.get("text") or seg.get("text_src")

            f.write(f"{i}\n")
            f.write(f"{format_srt_time(seg['start'])} --> {format_srt_time(seg['end'])}\n")
            f.write(f"{text}\n\n")


# ─────────────────────────────────────────────
# MAIN
# ─────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--audio-extract")
    parser.add_argument("--transcribe", action="store_true")
    parser.add_argument("--translate", action="store_true")
    parser.add_argument("--src-lang", default=None)
    parser.add_argument("--tgt-lang", default="vi")
    parser.add_argument("--segments-json")
    parser.add_argument("--srt-output")
    
    # THÊM CÁC DÒNG NÀY ĐỂ NHẬN LỆNH TỪ C#
    parser.add_argument("--tts", action="store_true")
    parser.add_argument("--tts-dir")
    parser.add_argument("--dubbed-audio")
    parser.add_argument("--video-duration", type=float, default=0.0)

    args = parser.parse_args()

    segments = []
    lang = args.src_lang or "en"

    if args.segments_json and os.path.exists(args.segments_json):
        with open(args.segments_json, "r", encoding="utf-8") as f:
            data = json.load(f)
            segments = data.get("segments", [])
            lang = data.get("language", lang)

    if args.audio_extract:
        subprocess.run([
            "ffmpeg", "-y", "-i", args.input,
            "-vn", "-acodec", "pcm_s16le",
            "-ar", "16000", "-ac", "1",
            args.audio_extract
        ], check=True)

    if args.transcribe:
        segments, lang = transcribe_audio(args.audio_extract, args.src_lang)

    if args.translate:
        segments = translate_segments(segments, lang, args.tgt_lang)

    if args.segments_json:
        with open(args.segments_json, "w", encoding="utf-8") as f:
            json.dump({"segments": segments, "language": lang}, f, ensure_ascii=False, indent=2)

    if args.srt_output:
        write_srt(segments, args.srt_output)
        
        # ... (code ghi srt_output ở trên) ...

    # XỬ LÝ LỒNG TIẾNG (TTS)
    if args.tts and args.tts_dir and args.dubbed_audio:
        log("Bắt đầu tạo TTS và ghép nối audio...")
        try:
            from pydub import AudioSegment
        except ImportError:
            subprocess.run([sys.executable, "-m", "pip", "install", "pydub", "-q"], check=True)
            from pydub import AudioSegment

        # 1. Tạo các file audio nhỏ cho từng segment
        segments = generate_tts_audio(segments, args.tgt_lang, args.tts_dir)

        # 2. Tạo một track im lặng (silent) bằng tổng thời lượng video
        base_audio = AudioSegment.silent(duration=int(args.video_duration * 1000))

        # 3. Đặt các câu thoại TTS vào đúng vị trí thời gian
        for seg in segments:
            audio_path = seg.get("audio_path")
            if audio_path and os.path.exists(audio_path):
                try:
                    seg_audio = AudioSegment.from_file(audio_path)
                    start_ms = int(seg["start"] * 1000)
                    base_audio = base_audio.overlay(seg_audio, position=start_ms)
                except Exception as e:
                    log(f"Lỗi ghép đoạn TTS: {e}", "WARN")

        # 4. Xuất file audio tổng (.aac hoặc .wav tùy vào cấu hình C#)
        export_format = "adts" if args.dubbed_audio.endswith(".aac") else "wav"
        base_audio.export(args.dubbed_audio, format=export_format)
        log(f"Đã xuất audio lồng tiếng: {args.dubbed_audio}")

if __name__ == "__main__":
    main()


if __name__ == "__main__":
    main()