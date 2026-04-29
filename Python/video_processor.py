#!/usr/bin/env python3
import sys
import os
import json
import argparse
import subprocess
import time  
import re

# Cấu hình encoding cho Windows để tránh lỗi hiển thị tiếng Việt
if sys.platform == "win32":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

def log(msg, level="INFO"):
    print(f"[{level}] {msg}", flush=True)

# ─────────────────────────────────────────────
# 1. TRANSCRIBE (Whisper)
# ─────────────────────────────────────────────
def transcribe_audio(audio_path, language=None):
    if not os.path.exists(audio_path):
        raise FileNotFoundError(f"Không tìm thấy file audio: {audio_path}")
    try:
        import whisper
    except ImportError:
        subprocess.run([sys.executable, "-m", "pip", "install", "openai-whisper", "-q"], check=True)
        import whisper

    log("Đang nạp mô hình Whisper (base)...")
    model = whisper.load_model("base")
    
    log(f"Đang nhận dạng giọng nói...")
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
# 2. TRANSLATE VỚI GEMINI AI (Dùng 2.0 Flash - Siêu Ổn Định)
# ─────────────────────────────────────────────
def translate_segments_gemini(segments, src_lang, tgt_lang, api_key):
    if src_lang == tgt_lang:
        for seg in segments: seg["text_src"] = seg["text"]
        return segments
    if not api_key:
        log("Thiếu API Key. Bỏ qua dịch thuật.", "WARN")
        return segments
    try:
        from google import genai
        from google.genai import types
    except ImportError:
        subprocess.run([sys.executable, "-m", "pip", "install", "google-genai", "-q"], check=True)
        from google import genai
        from google.genai import types

    log(f"Đang dịch thuật AI ({src_lang} -> {tgt_lang}) bằng Gemini 2.0 Flash...")
    client = genai.Client(api_key=api_key)
    
    sys_prompt = f"""Bạn là chuyên gia dịch thuật video chuyên nghiệp từ {src_lang} sang {tgt_lang}.
Yêu cầu: Dịch thoát ý, tự nhiên. Nếu là tiếng Trung (zh), dùng văn phong Hán Việt mượt mà.
Đầu ra là JSON array: [{{"id": 0, "text_translated": "..."}}]. Không dùng dấu ngoặc kép bên trong câu dịch."""

    batch_size = 20
    translated = []
    for i in range(0, len(segments), batch_size):
        batch = segments[i:i+batch_size]
        input_data = [{"id": idx, "text_original": seg["text"]} for idx, seg in enumerate(batch)]
        prompt = f"{sys_prompt}\n\nNội dung cần dịch:\n{json.dumps(input_data, ensure_ascii=False)}"
        
        success = False
        # Thử lại tối đa 5 lần nếu gặp lỗi quá tải
        for attempt in range(5):
            try:
                # Chuyển hẳn sang gemini-2.0-flash (bản ổn định nhất hiện tại)
                response = client.models.generate_content(
                    model='gemini-2.0-flash',
                    contents=prompt,
                    config=types.GenerateContentConfig(
                        response_mime_type="application/json", 
                        temperature=0.2
                    ),
                )
                
                res_text = response.text.strip()
                res_text = re.sub(r'^```json\s*|\s*```$', '', res_text) 
                parsed = json.loads(res_text)
                
                for j, seg in enumerate(batch):
                    item = next((x for x in parsed if x.get("id") == j), None)
                    s_copy = seg.copy()
                    s_copy["text_src"] = seg["text"]
                    s_copy["text"] = item.get("text_translated", seg["text"]) if item else seg["text"]
                    translated.append(s_copy)
                success = True
                break
            except Exception as e:
                err = str(e)
                # Lỗi 503 (Quá tải) hoặc 429 (Hết lượt)
                if "503" in err or "High demand" in err:
                    wait_time = 15 + (attempt * 5) # Tăng dần thời gian chờ: 15s, 20s, 25s...
                    log(f"Server Gemini 2.0 đang bận, chờ {wait_time}s rồi thử lại...", "INFO")
                    time.sleep(wait_time)
                elif "429" in err:
                    log("Đã hết lượt dùng API miễn phí (429). Chờ 30s...", "WARN")
                    time.sleep(30)
                else:
                    log(f"Lỗi dịch cụm {i} (Lần {attempt+1}): {err[:100]}", "WARN")
                    time.sleep(5)
        
        if not success:
            log(f"Bỏ qua dịch cụm {i} do server Google quá tải lâu ngày.", "ERROR")
            for seg in batch:
                sc = seg.copy(); sc["text_src"] = seg["text"]; translated.append(sc)
        
        # Nghỉ giữa các cụm để tránh bị Google "soi" spam
        time.sleep(5)
    return translated

# ─────────────────────────────────────────────
# 3. TTS (Piper Offline)
# ─────────────────────────────────────────────
def generate_tts_audio_offline(segments, tgt_lang, output_dir):
    import urllib.request
    import wave
    try:
        from piper import PiperVoice
    except ImportError:
        subprocess.run([sys.executable, "-m", "pip", "install", "piper-tts", "-q"], check=True)
        from piper import PiperVoice
    
    os.makedirs(output_dir, exist_ok=True)
    m_name = "vi_VN-vais1000-medium.onnx" if tgt_lang == "vi" else "en_US-lessac-medium.onnx"
    m_path = os.path.join(os.getcwd(), m_name)
    
    if not os.path.exists(m_path):
        log(f"Đang tải Model Piper...")
        # Link tải dự phòng rút gọn
        url_base = f"https://huggingface.co/rhasspy/piper-voices/resolve/main/{tgt_lang}/{tgt_lang.upper()}/{m_name.split('-')[0].replace('_','/')}/medium/"
        urllib.request.urlretrieve(url_base + m_name, m_path)
        urllib.request.urlretrieve(url_base + m_name + ".json", m_path + ".json")

    voice = PiperVoice.load(m_path)
    result = []
    for i, seg in enumerate(segments):
        t_clean = re.sub(r'[^\w\s\.,!\?]', '', seg["text"])
        if not t_clean.strip(): result.append({**seg, "audio_path": None}); continue
        path = os.path.join(output_dir, f"seg_{i:04d}.wav")
        with wave.open(path, 'wb') as f:
            f.setnchannels(1); f.setsampwidth(2); f.setframerate(voice.config.sample_rate)
            voice.synthesize(t_clean, f)
        result.append({**seg, "audio_path": path})
    return result

# ─────────────────────────────────────────────
# 4. MAIN
# ─────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True); parser.add_argument("--audio")
    parser.add_argument("--transcribe", action="store_true"); parser.add_argument("--translate", action="store_true")
    parser.add_argument("--api-key", default=""); parser.add_argument("--tgt-lang", default="vi")
    parser.add_argument("--segments-json"); parser.add_argument("--srt-output")
    parser.add_argument("--tts", action="store_true"); parser.add_argument("--tts-dir")
    parser.add_argument("--dubbed-audio"); parser.add_argument("--video-duration", type=float, default=0.0)
    args = parser.parse_args()

    segments, lang = [], "en"
    if args.segments_json and os.path.exists(args.segments_json):
        with open(args.segments_json, "r", encoding="utf-8") as f:
            data = json.load(f); segments = data.get("segments", []); lang = data.get("language", "en")

    if args.transcribe:
        segments, lang = transcribe_audio(args.audio)
    
    if args.translate:
        segments = translate_segments_gemini(segments, lang, args.tgt_lang, args.api_key)

    if args.segments_json:
        with open(args.segments_json, "w", encoding="utf-8") as f:
            json.dump({"segments": segments, "language": lang}, f, ensure_ascii=False, indent=2)

    if args.tts and args.tts_dir and args.dubbed_audio:
        try:
            from pydub import AudioSegment, effects
        except ImportError:
            subprocess.run([sys.executable, "-m", "pip", "install", "pydub", "-q"], check=True)
            from pydub import AudioSegment, effects

        log("Đang tạo giọng đọc Piper...")
        segments = generate_tts_audio_offline(segments, args.tgt_lang, args.tts_dir)
        base_audio = AudioSegment.silent(duration=max(int(args.video_duration * 1000), 1000))

        for seg in segments:
            if seg.get("audio_path"):
                s_audio = AudioSegment.from_wav(seg["audio_path"])
                start_ms, end_ms = int(seg["start"] * 1000), int(seg["end"] * 1000)
                limit = end_ms - start_ms
                if len(s_audio) > limit and limit > 0:
                    s_audio = effects.speedup(s_audio, playback_speed=min(len(s_audio)/limit, 2.0))
                base_audio = base_audio.overlay(s_audio, position=start_ms)

        temp_wav = args.dubbed_audio.replace(".aac", ".wav")
        base_audio.export(temp_wav, format="wav")
        # Gọi FFmpeg trộn file cuối
        subprocess.run(["ffmpeg", "-y", "-i", temp_wav, "-c:a", "aac", "-b:a", "192k", args.dubbed_audio], check=True)
        if os.path.exists(temp_wav): os.remove(temp_wav)
        log("Lồng tiếng hoàn tất!", "SUCCESS")

if __name__ == "__main__":
    try: main()
    except Exception as e:
        import traceback
        log(f"Lỗi hệ thống: {e}", "ERROR")
        traceback.print_exc()
        sys.exit(1)