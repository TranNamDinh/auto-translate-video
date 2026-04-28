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
# 1. TRANSCRIBE (TỐI ƯU HÓA BẰNG GPU CUDA & AUTO-INSTALL)
# ─────────────────────────────────────────────
def transcribe_audio(audio_path, language=None):
    try:
        from faster_whisper import WhisperModel
    except ImportError:
        log("Đang tự động cài đặt faster-whisper (chỉ chạy lần đầu)...", "INFO")
        subprocess.run([sys.executable, "-m", "pip", "install", "faster-whisper", "-q"], check=True)
        from faster_whisper import WhisperModel

    log("🚀 Đang nạp Faster-Whisper lên VRAM GPU...")
    
    # Khởi tạo model trên GPU. float16 giúp tăng gấp đôi tốc độ và tiết kiệm VRAM.
    try:
        model = WhisperModel("base", device="cuda", compute_type="float16")
    except Exception as e:
        log(f"⚠️ Không tìm thấy GPU hoặc thiếu CUDA, chuyển sang chạy bằng CPU: {e}", "WARN")
        model = WhisperModel("base", device="cpu", compute_type="int8")
        
    log("Bắt đầu bóc băng audio...")
    segments_gen, info = model.transcribe(audio_path, language=language)
    
    segments = []
    for seg in segments_gen:
        segments.append({
            "start": seg.start,
            "end": seg.end,
            "text": seg.text.strip()
        })
        log(f"[{seg.start:.2f}s -> {seg.end:.2f}s] {seg.text.strip()}", "DEBUG")

    return segments, info.language


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
# 3. TTS (LOCAL BẰNG PIPER TTS - OFFLINE 100%)
# ─────────────────────────────────────────────
def get_piper_model_url(lang):
    # Danh sách các model Piper chất lượng cao tải từ HuggingFace
    models = {
        # Tiếng Việt
        "vi": ("vi_VN-vivos-x_low.onnx", "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx"),
        
        # Tiếng Anh (Mỹ) - Giọng nữ Amy (Medium) cực kỳ tự nhiên và rõ chữ, chuẩn giọng review/kể chuyện
        "en": ("en_US-amy-medium.onnx", "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx"),
        
        # Tiếng Hàn (Hàn Quốc) - Giọng nữ KSS chuẩn Seoul, rất phù hợp cho video fitness hoặc vlog
        "ko": ("ko_KR-kss-medium.onnx", "https://huggingface.co/rhasspy/piper-voices/resolve/main/ko/ko_KR/kss/medium/ko_KR-kss-medium.onnx")
    }
    return models.get(lang, models["vi"]) # Mặc định lấy tiếng Việt nếu dropdown gửi ngôn ngữ lạ

def ensure_piper_model(model_name, url):
    import urllib.request
    
    # Hàm này tự động tải file Model và file JSON cấu hình nếu máy chưa có
    if not os.path.exists(model_name):
        log(f"Đang tải model giọng đọc ({model_name})...", "INFO")
        urllib.request.urlretrieve(url, model_name)
    
    json_name = f"{model_name}.json"
    if not os.path.exists(json_name):
        json_url = f"{url}.json"
        urllib.request.urlretrieve(json_url, json_name)

def generate_tts_audio(segments, tgt_lang, output_dir):
    import re
    import wave
    os.makedirs(output_dir, exist_ok=True)
    
    # 1. Tự động cài đặt thư viện Piper TTS nếu chưa có
    try:
        from piper.voice import PiperVoice
    except ImportError:
        log("Đang tự động cài đặt piper-tts (chỉ chạy lần đầu)...", "INFO")
        subprocess.run([sys.executable, "-m", "pip", "install", "piper-tts", "-q"], check=True)
        from piper.voice import PiperVoice

    # 2. Kiểm tra và tải model về máy
    model_name, model_url = get_piper_model_url(tgt_lang)
    ensure_piper_model(model_name, model_url)
    
    # 3. Nạp model vào bộ nhớ để chuẩn bị đọc
    log(f"Đang nạp model Piper TTS ({model_name}) vào RAM...", "INFO")
    voice = PiperVoice.load(model_name)
    
    result = []
    log("Bắt đầu sinh âm thanh lồng tiếng (Offline)...")
    
    for i, seg in enumerate(segments):
        text = seg["text"].strip()
        
        # Xóa ký tự đặc biệt để tránh lỗi bộ đọc
        text_clean = re.sub(r'[^\w\s]', '', text)
        if not text_clean.strip():
            result.append({**seg, "audio_path": None})
            continue

        # CHÚ Ý: Piper sinh ra file .wav (chất lượng cao) thay vì .mp3
        path = os.path.join(output_dir, f"seg_{i:04d}.wav")
        
        try:
            # Render ra file audio ngay lập tức
            with wave.open(path, "wb") as wav_file:
                voice.synthesize(text, wav_file)
            result.append({**seg, "audio_path": path})
            
        except Exception as e:
            log(f"❌ Lỗi TTS [{text}]: {e}", "WARN")
            result.append({**seg, "audio_path": None})
            
    return result


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