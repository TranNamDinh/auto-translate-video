#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import subprocess
import sys
import os

PACKAGES = [
    "openai-whisper",
    "edge-tts",
    "argostranslate",
    "torch",
    "tqdm",
    "numpy",
]

def install(pkg):
    print(f"Installing {pkg}...", flush=True)
    result = subprocess.run(
        [sys.executable, "-m", "pip", "install", pkg, "--quiet"],
        capture_output=True, text=True
    )
    if result.returncode == 0:
        print(f"  OK: {pkg}", flush=True)
    else:
        print(f"  FAIL: {pkg}: {result.stderr[:200]}", flush=True)

def check_ffmpeg():
    try:
        result = subprocess.run(
            ["ffmpeg", "-version"],
            capture_output=True, text=True
        )
        if result.returncode == 0:
            print("OK: FFmpeg found", flush=True)
            return True
    except FileNotFoundError:
        pass
    print("WARN: FFmpeg NOT found. Please install FFmpeg and add to PATH.", flush=True)
    return False

if __name__ == "__main__":
    # Fix encoding cho Windows
    if sys.platform == "win32":
        import io
        sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
        sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

    print("=== Video Processor Setup ===", flush=True)
    print(f"Python: {sys.version}", flush=True)

    print("[1/2] Checking FFmpeg...", flush=True)
    check_ffmpeg()

    print("[2/2] Installing Python packages...", flush=True)
    for pkg in PACKAGES:
        install(pkg)

    print("Setup complete!", flush=True)