@echo off
title Build Speaker STT Test Application
echo.
echo ================================================
echo   🔨 Сборка Speaker STT Test Application
echo ================================================
echo.

echo 📦 Восстановление NuGet пакетов...
dotnet restore
if errorlevel 1 (
    echo ❌ Ошибка восстановления пакетов!
    pause
    exit /b 1
)

echo.
echo 🔨 Сборка проекта в Release конфигурации...
dotnet build --configuration Release --no-restore
if errorlevel 1 (
    echo ❌ Ошибка сборки проекта!
    pause
    exit /b 1
)

echo.
echo ✅ Сборка завершена успешно!
echo.
echo 📂 Исполняемый файл: bin\Release\net8.0-windows\test_speaker_stt_translate_tts.exe
echo.
echo Нажмите любую клавишу для запуска приложения...
pause > nul

echo 🚀 Запуск приложения...
call run_app.bat