@echo off
REM =====================================================
REM  Video Processor - FFmpeg Check & Setup Helper
REM =====================================================

echo Checking FFmpeg...
ffmpeg -version >nul 2>&1
if %errorlevel% == 0 (
    echo [OK] FFmpeg is installed.
) else (
    echo [WARN] FFmpeg not found in PATH!
    echo.
    echo Please install FFmpeg:
    echo  1. Download from: https://www.gyan.dev/ffmpeg/builds/
    echo     Get: ffmpeg-release-essentials.zip
    echo  2. Extract to C:\ffmpeg\
    echo  3. Add C:\ffmpeg\bin to your System PATH
    echo.
    echo Or use winget:
    echo   winget install Gyan.FFmpeg
    echo.
    pause
    exit /b 1
)

echo.
echo Checking Python...
python --version >nul 2>&1
if %errorlevel% == 0 (
    python --version
    echo [OK] Python is installed.
) else (
    echo [WARN] Python not found!
    echo.
    echo Please install Python 3.10+:
    echo  https://www.python.org/downloads/
    echo  Make sure to check "Add Python to PATH"
    echo.
    pause
    exit /b 1
)

echo.
echo Installing Python dependencies...
python Python\setup.py

echo.
echo =====================================================
echo  Setup complete! You can now run VideoProcessor.exe
echo =====================================================
pause
