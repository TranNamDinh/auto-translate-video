#!/usr/bin/env python3
"""
Video Processor - Transcribe, Translate, TTS
Free, local processing using Whisper + Argos Translate + Edge-TTS
"""

import sys
import os
import json
import asyncio
import argparse
import subprocess
import tempfile
import re
from pathlib import Path

# Fix Windows encoding
if sys.platform == "win32":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

def log(msg, level="INFO"):
    print(f"[{level}] {msg}", flush=True)


# ─────────────────────────────────────────────
# 1. TRANSCRIBE (Whisper)
# ─────────────────────────────────────────────
def transcribe_audio(audio_path: str, language: str = None) -> list[dict]:
    """
    Returns list of segments: [{start, end, text}, ...]
    Uses openai-whisper (local, free).
    """
    try:
        import whisper
    except ImportError:
        log("Installing whisper...", "SETUP")
        subprocess.run([sys.executable, "-m", "pip", "install", "openai-whisper", "-q"], check=True)
        import whisper

    log("Loading Whisper model (base)...")
    model = whisper.load_model("base")
    
    log(f"Transcribing: {audio_path}")
    opts = {"word_timestamps": False}
    if language:
        opts["language"] = language
    
    result = model.transcribe(audio_path, **opts)
    
    segments = []
    for seg in result["segments"]:
        segments.append({
            "start": seg["start"],
            "end": seg["end"],
            "text": seg["text"].strip()
        })
    
    log(f"Transcribed {len(segments)} segments. Language: {result.get('language', 'unknown')}")
    return segments, result.get("language", "en")


# ─────────────────────────────────────────────
# 2. TRANSLATE (Argos Translate - free, local)
# ─────────────────────────────────────────────
def translate_segments(segments: list[dict], src_lang: str, tgt_lang: str) -> list[dict]:
    """
    Translate text in segments using Argos Translate (fully local, free).
    Falls back to returning original if same language.
    """
    if src_lang == tgt_lang:
        log("Source and target language are the same, skipping translation.")
        return segments

    try:
        import argostranslate.package
        import argostranslate.translate
    except ImportError:
        log("Installing argos-translate...", "SETUP")
        subprocess.run([sys.executable, "-m", "pip", "install", "argostranslate", "-q"], check=True)
        import argostranslate.package
        import argostranslate.translate

    # Check if language pair installed
    log(f"Setting up translation: {src_lang} -> {tgt_lang}")
    argostranslate.package.update_package_index()
    available = argostranslate.package.get_available_packages()
    
    pkg = next(
        (p for p in available if p.from_code == src_lang and p.to_code == tgt_lang),
        None
    )
    
    if pkg is None:
        # Try via English as pivot
        log(f"Direct pair {src_lang}→{tgt_lang} not found, trying via English pivot...", "WARN")
        # Just translate each segment
        translated = []
        for seg in segments:
            translated.append({**seg, "text": seg["text"]})
        return translated
    
    # Install if not already
    installed = argostranslate.package.get_installed_packages()
    installed_pairs = [(p.from_code, p.to_code) for p in installed]
    
    if (src_lang, tgt_lang) not in installed_pairs:
        log(f"Downloading language pack {src_lang}→{tgt_lang}...")
        argostranslate.package.install_from_path(pkg.download())
    
    log("Translating segments...")
    translated = []
    for seg in segments:
        translated_text = argostranslate.translate.translate(seg["text"], src_lang, tgt_lang)
        translated.append({**seg, "text": translated_text})
    
    log(f"Translated {len(translated)} segments.")
    return translated


# ─────────────────────────────────────────────
# 3. TEXT-TO-SPEECH (Edge-TTS - free Microsoft Azure voices)
# ─────────────────────────────────────────────
async def tts_segment_async(text: str, voice: str, output_path: str, rate: str = "+0%"):
    """Generate TTS audio for a single segment."""
    import edge_tts
    communicate = edge_tts.Communicate(text, voice, rate=rate)
    await communicate.save(output_path)


def get_voice_for_language(lang: str) -> str:
    """Return best Edge-TTS voice for language."""
    voice_map = {
        "vi": "vi-VN-NamMinhNeural",       # Vietnamese male
        "en": "en-US-GuyNeural",            # English male
        "zh": "zh-CN-YunxiNeural",          # Chinese
        "ja": "ja-JP-KeitaNeural",          # Japanese
        "ko": "ko-KR-InJoonNeural",         # Korean
        "fr": "fr-FR-HenriNeural",          # French
        "de": "de-DE-ConradNeural",         # German
        "es": "es-ES-AlvaroNeural",         # Spanish
        "ru": "ru-RU-DmitryNeural",         # Russian
        "th": "th-TH-NiwatNeural",          # Thai
        "id": "id-ID-ArdiNeural",           # Indonesian
        "pt": "pt-BR-AntonioNeural",        # Portuguese
    }
    return voice_map.get(lang, "en-US-GuyNeural")


def generate_tts_audio(segments: list[dict], tgt_lang: str, output_dir: str) -> list[dict]:
    """
    Generate TTS audio for each segment.
    Returns segments with added 'audio_path' field.
    """
    try:
        import edge_tts
    except ImportError:
        log("Installing edge-tts...", "SETUP")
        subprocess.run([sys.executable, "-m", "pip", "install", "edge-tts", "-q"], check=True)
        import edge_tts

    voice = get_voice_for_language(tgt_lang)
    log(f"Using TTS voice: {voice}")
    
    result_segments = []
    os.makedirs(output_dir, exist_ok=True)
    
    for i, seg in enumerate(segments):
        if not seg["text"].strip():
            result_segments.append({**seg, "audio_path": None})
            continue
        
        audio_path = os.path.join(output_dir, f"seg_{i:04d}.mp3")
        
        try:
            asyncio.run(tts_segment_async(seg["text"], voice, audio_path))
            result_segments.append({**seg, "audio_path": audio_path})
            log(f"TTS [{i+1}/{len(segments)}]: {seg['text'][:50]}...")
        except Exception as e:
            log(f"TTS failed for segment {i}: {e}", "WARN")
            result_segments.append({**seg, "audio_path": None})
    
    return result_segments


# ─────────────────────────────────────────────
# 4. BUILD SRT SUBTITLE FILE
# ─────────────────────────────────────────────
def format_srt_time(seconds: float) -> str:
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = int(seconds % 60)
    ms = int((seconds - int(seconds)) * 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def write_srt(segments: list[dict], srt_path: str):
    with open(srt_path, "w", encoding="utf-8") as f:
        for i, seg in enumerate(segments, 1):
            f.write(f"{i}\n")
            f.write(f"{format_srt_time(seg['start'])} --> {format_srt_time(seg['end'])}\n")
            f.write(f"{seg['text']}\n\n")
    log(f"SRT written: {srt_path}")


# ─────────────────────────────────────────────
# 5. MERGE TTS AUDIO INTO TIMELINE
# ─────────────────────────────────────────────
def build_dubbed_audio(segments: list[dict], video_duration: float, output_path: str):
    """
    Merge TTS segments into a single audio track aligned to video timeline.
    Uses FFmpeg with adelay filter.
    """
    log("Building dubbed audio track...")
    
    valid_segs = [(i, s) for i, s in enumerate(segments) if s.get("audio_path") and os.path.exists(s["audio_path"])]
    
    if not valid_segs:
        log("No valid TTS segments found.", "WARN")
        # Create silence
        subprocess.run([
            "ffmpeg", "-y", "-f", "lavfi",
            f"-i", f"anullsrc=r=44100:cl=stereo",
            "-t", str(video_duration),
            output_path
        ], check=True, capture_output=True)
        return

    # Build complex filter
    inputs = []
    filter_parts = []
    
    for idx, (seg_idx, seg) in enumerate(valid_segs):
        inputs.extend(["-i", seg["audio_path"]])
        delay_ms = int(seg["start"] * 1000)
        filter_parts.append(f"[{idx}]adelay={delay_ms}|{delay_ms}[a{idx}]")
    
    # Mix all
    mix_inputs = "".join(f"[a{i}]" for i in range(len(valid_segs)))
    filter_parts.append(f"{mix_inputs}amix=inputs={len(valid_segs)}:normalize=0[out]")
    
    filter_complex = ";".join(filter_parts)
    
    cmd = ["ffmpeg", "-y"]
    cmd.extend(inputs)
    cmd.extend([
        "-filter_complex", filter_complex,
        "-map", "[out]",
        "-t", str(video_duration),
        "-ar", "44100",
        "-ac", "2",
        output_path
    ])
    
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        log(f"FFmpeg error: {result.stderr}", "ERROR")
        raise RuntimeError("Failed to build dubbed audio")
    
    log(f"Dubbed audio: {output_path}")


# ─────────────────────────────────────────────
# MAIN PIPELINE
# ─────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser(description="Video Processor Pipeline")
    parser.add_argument("--input", required=True, help="Input video path")
    parser.add_argument("--audio-extract", help="Extract audio to this path")
    parser.add_argument("--transcribe", action="store_true", help="Run transcription")
    parser.add_argument("--translate", action="store_true", help="Run translation")
    parser.add_argument("--src-lang", default=None, help="Source language code")
    parser.add_argument("--tgt-lang", default="vi", help="Target language code")
    parser.add_argument("--tts", action="store_true", help="Generate TTS audio")
    parser.add_argument("--tts-dir", help="Directory for TTS audio files")
    parser.add_argument("--dubbed-audio", help="Output dubbed audio path")
    parser.add_argument("--srt-output", help="Output SRT path")
    parser.add_argument("--segments-json", help="Path to save/load segments JSON")
    parser.add_argument("--video-duration", type=float, default=0, help="Video duration in seconds")
    
    args = parser.parse_args()
    
    segments = []
    detected_lang = args.src_lang or "en"
    
    # Load existing segments if available
    if args.segments_json and os.path.exists(args.segments_json):
        with open(args.segments_json, "r", encoding="utf-8") as f:
            data = json.load(f)
            segments = data.get("segments", [])
            detected_lang = data.get("language", detected_lang)
            log(f"Loaded {len(segments)} segments from {args.segments_json}")
    
    # Extract audio
    if args.audio_extract:
        log(f"Extracting audio from video...")
        subprocess.run([
            "ffmpeg", "-y", "-i", args.input,
            "-vn", "-acodec", "pcm_s16le",
            "-ar", "16000", "-ac", "1",
            args.audio_extract
        ], check=True, capture_output=True)
        log(f"Audio extracted: {args.audio_extract}")
    
    # Transcribe
    if args.transcribe and args.audio_extract and os.path.exists(args.audio_extract):
        segments, detected_lang = transcribe_audio(args.audio_extract, args.src_lang)
        if args.segments_json:
            with open(args.segments_json, "w", encoding="utf-8") as f:
                json.dump({"segments": segments, "language": detected_lang}, f, ensure_ascii=False, indent=2)
    
    # Translate
    if args.translate and segments:
        src = detected_lang
        tgt = args.tgt_lang
        segments = translate_segments(segments, src, tgt)
        if args.segments_json:
            with open(args.segments_json, "w", encoding="utf-8") as f:
                json.dump({"segments": segments, "language": detected_lang}, f, ensure_ascii=False, indent=2)
    
    # Write SRT
    if args.srt_output and segments:
        write_srt(segments, args.srt_output)
    
    # TTS
    if args.tts and segments and args.tts_dir:
        segments = generate_tts_audio(segments, args.tgt_lang, args.tts_dir)
        if args.segments_json:
            with open(args.segments_json, "w", encoding="utf-8") as f:
                json.dump({"segments": segments, "language": detected_lang}, f, ensure_ascii=False, indent=2)
    
    # Build dubbed audio
    if args.dubbed_audio and segments and args.video_duration > 0:
        build_dubbed_audio(segments, args.video_duration, args.dubbed_audio)
    
    log("Pipeline step completed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
