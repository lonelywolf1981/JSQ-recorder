@echo off
REM Скрипт сборки JSQ в папку debug_qwen

echo ========================================
echo JSQ Build Script - Debug_Qwen
echo ========================================
echo.

cd /d "%~dp0JSQ"

echo [1/2] Очистка предыдущей сборки...
dotnet clean -v q >nul 2>&1

echo [2/2] Сборка проекта...
dotnet build JSQ.UI.WPF/JSQ.UI.WPF.csproj -c Debug_Qwen -o ..\..\debug_qwen -v q

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Сборка не удалась!
    exit /b 1
)

echo.
echo ========================================
echo Сборка завершена успешно!
echo Путь: %~dp0debug_qwen\
echo ========================================
echo.
echo Файлы:
dir "..\debug_qwen\*.exe" /b 2>nul
echo.
echo Для запуска:
echo   %~dp0debug_qwen\JSQ.UI.WPF.exe
echo.
