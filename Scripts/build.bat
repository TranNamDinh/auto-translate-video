@echo off
cd /d "%~dp0.."
echo Building VideoProcessor...

dotnet publish WinForm\VideoProcessor.csproj -c Release -r win-x64 --self-contained false -o dist\
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b 1
)

mkdir dist\Python 2>nul
copy Python\*.py dist\Python\
copy Scripts\setup_windows.bat dist\

echo.
echo Build complete! Chay dist\VideoProcessor.exe
pause