@echo off
title Speaker STT Test Application
echo.
echo ================================================
echo   🎧 Speaker STT → Translate → TTS Tester
echo ================================================
echo.
echo Запуск тестового приложения...
echo.

cd /d "%~dp0bin\Release\net8.0-windows"

if not exist "test_speaker_stt_translate_tts.exe" (
    echo ❌ Исполняемый файл не найден!
    echo Сначала выполните: dotnet build --configuration Release
    echo.
    pause
    exit /b 1
)

echo ✅ Запуск приложения...
echo.
start "" "test_speaker_stt_translate_tts.exe"

echo Приложение запущено!
echo Закройте это окно или нажмите любую клавишу...
pause > nul