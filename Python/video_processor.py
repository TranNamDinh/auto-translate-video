#!/usr/bin/env python3
"""
video_processor.py  ─ GPU-optimised batch video processor
==========================================================

Progress protocol (JSON lines on stdout — C# parses these):
  {"type":"progress","step":"Transcribe","pct":40}
  {"type":"log","level":"INFO","msg":"..."}

All other stdout lines are treated as plain debug text by the C# host.

Model caching strategy:
  ─ Whisper & Piper are loaded ONCE per process invocation and reused.
  ─ Since C# spawns one Python process PER VIDEO, caching lives in module-level
    globals so repeated calls within a single video's pipeline reuse them.
  ─ For true multi-video GPU sharing, run with --batch-mode and pass a
    JSON list of work items; this avoids the model load/unload cycle.
"""

import sys, os, json, argparse, subprocess, gc, re, wave, time
from pathlib import Path

# ── Windows UTF-8 stdout ─────────────────────────────────────────────────
if sys.platform == "win32":
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")

# ── Module-level model cache ─────────────────────────────────────────────
_whisper_model = None
_piper_voice   = None
_piper_lang    = None      # which language the loaded Piper model covers


# ════════════════════════════════════════════════════════════════════════════
#  LOGGING / PROGRESS HELPERS
# ════════════════════════════════════════════════════════════════════════════

def log(msg: str, level: str = "INFO") -> None:
    """Emit a plain log line (C# logs as Debug)."""
    print(f"[{level}] {msg}", flush=True)


def progress(step: str, pct: int = -1) -> None:
    """Emit a JSON progress line that C# parses into the per-file status strip."""
    obj = {"type": "progress", "step": step}
    if pct >= 0:
        obj["pct"] = pct
    print(json.dumps(obj, ensure_ascii=False), flush=True)


def _pip_install(*packages: str) -> None:
    subprocess.run(
        [sys.executable, "-m", "pip", "install", *packages, "-q"],
        check=True
    )


# ════════════════════════════════════════════════════════════════════════════
#  1.  TRANSCRIBE  ─ Faster-Whisper with CUDA, cached in _whisper_model
# ════════════════════════════════════════════════════════════════════════════

def _load_whisper():
    """Load Faster-Whisper once; prefer CUDA, fall back to CPU int8."""
    global _whisper_model
    if _whisper_model is not None:
        return _whisper_model

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        log("Auto-installing faster-whisper …")
        _pip_install("faster-whisper")
        from faster_whisper import WhisperModel

    progress("Nạp Whisper vào GPU…")
    try:
        _whisper_model = WhisperModel("base", device="cuda", compute_type="float16")
        log("Whisper loaded on CUDA fp16")
    except Exception as e:
        log(f"CUDA unavailable ({e}), falling back to CPU int8", "WARN")
        _whisper_model = WhisperModel("base", device="cpu", compute_type="int8")

    return _whisper_model


def transcribe_audio(audio_path: str, language: str | None = None) -> tuple[list, str]:
    model = _load_whisper()

    progress("Nhận dạng giọng nói…", 5)
    log(f"Transcribing: {audio_path} (lang={language or 'auto'})")

    # vad_filter=True removes hallucinated text during silence
    segments_gen, info = model.transcribe(
        audio_path,
        language=language or None,
        beam_size=5,
        vad_filter=True,
        vad_parameters={"min_silence_duration_ms": 500}
    )

    segments: list[dict] = []
    for seg in segments_gen:
        segments.append({"start": seg.start, "end": seg.end, "text": seg.text.strip()})
        log(f"  [{seg.start:.1f}s→{seg.end:.1f}s] {seg.text.strip()}", "DEBUG")

    progress("Nhận dạng xong", 100)

    # NOTE: we do NOT delete the model here — C# may call us again for the
    # next pipeline step within the same video. _release_models() is called
    # explicitly at the end of main() after all steps are done.

    return segments, info.language


# ════════════════════════════════════════════════════════════════════════════
#  2.  TRANSLATE  ─ Argos via English bridge, with model install on demand
# ════════════════════════════════════════════════════════════════════════════

_argos_installed: set[tuple[str, str]] = set()   # (src, tgt) pairs already verified


def _ensure_argos_model(src: str, tgt: str) -> None:
    global _argos_installed
    if (src, tgt) in _argos_installed:
        return

    try:
        import argostranslate.package
    except ImportError:
        _pip_install("argostranslate")
        import argostranslate.package

    argostranslate.package.update_package_index()
    available = argostranslate.package.get_available_packages()
    pkg = next((p for p in available if p.from_code == src and p.to_code == tgt), None)
    if not pkg:
        log(f"No Argos model for {src}→{tgt}", "WARN")
        return

    installed_pairs = {(p.from_code, p.to_code) for p in argostranslate.package.get_installed_packages()}
    if (src, tgt) not in installed_pairs:
        log(f"Installing translation model {src}→{tgt} …")
        argostranslate.package.install_from_path(pkg.download())

    _argos_installed.add((src, tgt))


def translate_segments(segments: list[dict], src_lang: str, tgt_lang: str) -> list[dict]:
    import argostranslate.translate

    # Normalise Chinese variants
    if src_lang.lower().startswith("zh"):
        src_lang = "zh"

    log(f"Translate: {src_lang} → {tgt_lang}")
    progress("Dịch…", 5)

    # Prepare required models
    if src_lang != tgt_lang:
        if src_lang != "en":
            _ensure_argos_model(src_lang, "en")
        if tgt_lang != "en":
            _ensure_argos_model("en", tgt_lang)

    translated: list[dict] = []
    total = len(segments)

    for i, seg in enumerate(segments):
        text = seg["text"].strip()
        if not text:
            translated.append({**seg, "text": ""})
            continue

        try:
            if src_lang == tgt_lang:
                final = text
            elif src_lang == "en":
                final = argostranslate.translate.translate(text, "en", tgt_lang)
            elif tgt_lang == "en":
                final = argostranslate.translate.translate(text, src_lang, "en")
            else:
                en_text = argostranslate.translate.translate(text, src_lang, "en")
                final = argostranslate.translate.translate(en_text, "en", tgt_lang)
        except Exception as e:
            log(f"Translate error [{text[:30]}]: {e}", "WARN")
            final = text

        translated.append({**seg, "text_src": text, "text": final})
        pct = int((i + 1) / total * 100)
        if pct % 10 == 0:
            progress("Dịch…", pct)

    progress("Dịch xong", 100)
    return translated


# ════════════════════════════════════════════════════════════════════════════
#  3.  TTS  ─ Piper (offline) with model cache + download on demand
# ════════════════════════════════════════════════════════════════════════════

# Map language → (model_filename, HuggingFace URL)
PIPER_MODELS: dict[str, tuple[str, str]] = {
    "vi": (
        "vi_VN-vivos-x_low.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/vi/vi_VN/vivos/x_low/vi_VN-vivos-x_low.onnx"
    ),
    "en": (
        "en_US-amy-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/amy/medium/en_US-amy-medium.onnx"
    ),
    "ko": (
        "ko_KR-kss-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/ko/ko_KR/kss/medium/ko_KR-kss-medium.onnx"
    ),
    "ja": (
        "ja_JP-kokoro-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/ja/ja_JP/kokoro/medium/ja_JP-kokoro-medium.onnx"
    ),
    "zh": (
        "zh_CN-huayan-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/zh/zh_CN/huayan/medium/zh_CN-huayan-medium.onnx"
    ),
    "fr": (
        "fr_FR-mls-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/fr/fr_FR/mls/medium/fr_FR-mls-medium.onnx"
    ),
    "de": (
        "de_DE-thorsten-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx"
    ),
    "es": (
        "es_ES-mls_10246-medium.onnx",
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/es_ES/mls_10246/medium/es_ES-mls_10246-medium.onnx"
    ),
}

MODELS_DIR = Path(os.environ.get("PIPER_MODELS_DIR", Path.home() / ".cache" / "piper_models"))


def _download_piper_model(model_name: str, url: str) -> Path:
    """Download ONNX model + JSON config if not cached."""
    import urllib.request

    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    model_path = MODELS_DIR / model_name
    json_path  = MODELS_DIR / (model_name + ".json")

    if not model_path.exists():
        log(f"Tải Piper model: {model_name} …")
        urllib.request.urlretrieve(url, model_path)

    if not json_path.exists():
        log(f"Tải Piper config: {model_name}.json …")
        urllib.request.urlretrieve(url + ".json", json_path)

    return model_path


def _load_piper(tgt_lang: str):
    """Load Piper voice, reusing cache if same language."""
    global _piper_voice, _piper_lang

    if _piper_voice is not None and _piper_lang == tgt_lang:
        return _piper_voice

    # Unload previous language model to free RAM
    if _piper_voice is not None:
        log(f"Unloading Piper model for '{_piper_lang}'")
        del _piper_voice
        gc.collect()
        _piper_voice = None

    try:
        from piper.voice import PiperVoice
    except ImportError:
        log("Auto-installing piper-tts …")
        _pip_install("piper-tts")
        from piper.voice import PiperVoice

    model_info = PIPER_MODELS.get(tgt_lang, PIPER_MODELS["vi"])
    model_path = _download_piper_model(*model_info)

    log(f"Nạp Piper TTS model: {model_path.name}")
    progress("Nạp TTS model…")
    _piper_voice = PiperVoice.load(str(model_path))
    _piper_lang  = tgt_lang

    return _piper_voice


def generate_tts_audio(segments: list[dict], tgt_lang: str, output_dir: str) -> list[dict]:
    os.makedirs(output_dir, exist_ok=True)
    voice = _load_piper(tgt_lang)

    result: list[dict] = []
    total = len(segments)
    progress("Tạo lồng tiếng…", 5)

    for i, seg in enumerate(segments):
        text = re.sub(r"[^\w\s]", "", seg["text"].strip())
        if not text:
            result.append({**seg, "audio_path": None})
            continue

        path = os.path.join(output_dir, f"seg_{i:04d}.wav")
        try:
            with wave.open(path, "wb") as wf:
                voice.synthesize(text, wf)
            result.append({**seg, "audio_path": path})
        except Exception as e:
            log(f"TTS error [{text[:30]}]: {e}", "WARN")
            result.append({**seg, "audio_path": None})

        pct = int((i + 1) / total * 100)
        if pct % 10 == 0:
            progress("Tạo lồng tiếng…", pct)

    progress("TTS xong", 100)
    return result


# ════════════════════════════════════════════════════════════════════════════
#  4.  SRT  ─ format & write
# ════════════════════════════════════════════════════════════════════════════

def _fmt_srt(s: float) -> str:
    h = int(s // 3600)
    m = int((s % 3600) // 60)
    sec = int(s % 60)
    ms = int((s - int(s)) * 1000)
    return f"{h:02}:{m:02}:{sec:02},{ms:03}"


def write_srt(segments: list[dict], path: str) -> None:
    with open(path, "w", encoding="utf-8") as f:
        for i, seg in enumerate(segments, 1):
            text = seg.get("text") or seg.get("text_src", "")
            f.write(f"{i}\n{_fmt_srt(seg['start'])} --> {_fmt_srt(seg['end'])}\n{text}\n\n")
    log(f"SRT written → {path}")


# ════════════════════════════════════════════════════════════════════════════
#  5.  AUDIO MIX  ─ overlay dubbed segments onto a silent base
# ════════════════════════════════════════════════════════════════════════════

def mix_dubbed_audio(segments: list[dict], video_duration: float, dubbed_audio_path: str) -> None:
    try:
        from pydub import AudioSegment
    except ImportError:
        _pip_install("pydub")
        from pydub import AudioSegment

    progress("Ghép audio lồng tiếng…", 10)
    base = AudioSegment.silent(duration=int(video_duration * 1000))

    for seg in segments:
        ap = seg.get("audio_path")
        if ap and os.path.exists(ap):
            try:
                seg_audio = AudioSegment.from_file(ap)
                base = base.overlay(seg_audio, position=int(seg["start"] * 1000))
            except Exception as e:
                log(f"Overlay error: {e}", "WARN")

    # Export — use 'adts' container for AAC
    fmt = "adts" if dubbed_audio_path.endswith(".aac") else "wav"
    base.export(dubbed_audio_path, format=fmt)

    del base
    gc.collect()

    progress("Ghép audio xong", 100)
    log(f"Dubbed audio → {dubbed_audio_path}")


# ════════════════════════════════════════════════════════════════════════════
#  6.  RELEASE  ─ free GPU/RAM after all steps done
# ════════════════════════════════════════════════════════════════════════════

def _release_models() -> None:
    global _whisper_model, _piper_voice, _piper_lang

    if _whisper_model is not None:
        log("Releasing Whisper from VRAM…")
        del _whisper_model
        _whisper_model = None

    if _piper_voice is not None:
        log("Releasing Piper from RAM…")
        del _piper_voice
        _piper_voice  = None
        _piper_lang   = None

    gc.collect()

    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            log(f"VRAM after release: {torch.cuda.memory_allocated()/1e6:.1f} MB allocated")
    except ImportError:
        pass


# ════════════════════════════════════════════════════════════════════════════
#  MAIN
# ════════════════════════════════════════════════════════════════════════════

def main() -> None:
    parser = argparse.ArgumentParser(description="Video Processor — GPU-optimised pipeline")
    parser.add_argument("--input",          required=True,  help="Input video path (safe/temp copy)")
    parser.add_argument("--audio",                          help="Extracted WAV audio path")
    parser.add_argument("--transcribe",     action="store_true")
    parser.add_argument("--translate",      action="store_true")
    parser.add_argument("--src-lang",       default=None)
    parser.add_argument("--tgt-lang",       default="vi")
    parser.add_argument("--segments-json",                  help="Path to segments state file")
    parser.add_argument("--srt-output",                     help="Write SRT here")
    parser.add_argument("--tts",            action="store_true")
    parser.add_argument("--tts-dir",                        help="Directory for per-segment WAVs")
    parser.add_argument("--dubbed-audio",                   help="Final mixed dubbed audio output")
    parser.add_argument("--video-duration", type=float, default=0.0)
    args = parser.parse_args()

    # ── Load persisted segment state (if any) ────────────────────────────
    segments: list[dict] = []
    lang: str = args.src_lang or "en"

    if args.segments_json and os.path.exists(args.segments_json):
        with open(args.segments_json, "r", encoding="utf-8") as f:
            data = json.load(f)
            segments = data.get("segments", [])
            lang     = data.get("language", lang)

    # ── Pipeline steps ───────────────────────────────────────────────────
    try:
        if args.transcribe:
            if not args.audio:
                raise ValueError("--audio is required with --transcribe")
            segments, lang = transcribe_audio(args.audio, args.src_lang or None)

        if args.translate:
            if not segments:
                log("No segments to translate", "WARN")
            else:
                segments = translate_segments(segments, lang, args.tgt_lang)

        # Persist segment state after any modification
        if args.segments_json and (args.transcribe or args.translate):
            with open(args.segments_json, "w", encoding="utf-8") as f:
                json.dump({"segments": segments, "language": lang}, f, ensure_ascii=False, indent=2)

        if args.srt_output:
            write_srt(segments, args.srt_output)

        if args.tts and args.tts_dir and args.dubbed_audio:
            if not segments:
                log("No segments for TTS", "WARN")
            else:
                segments = generate_tts_audio(segments, args.tgt_lang, args.tts_dir)
                mix_dubbed_audio(segments, args.video_duration, args.dubbed_audio)

    finally:
        # Always release GPU/RAM at the very end of this process invocation
        _release_models()


if __name__ == "__main__":
    main()