@echo off
setlocal
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 9 SDK is required. Install it and run this script again.
  pause
  exit /b 1
)
dotnet --list-sdks | findstr /b /l /c:"9." >nul
if errorlevel 1 (
  echo .NET 9 SDK was not found. The Desktop Runtime alone cannot build this app.
  pause
  exit /b 1
)
dotnet publish "%~dp0ChatAssistant\ChatAssistant.csproj" -c Release -o "%~dp0app" --nologo
if errorlevel 1 (
  echo Build failed. Close ChatAssistant if it is running, then check the errors above.
  pause
  exit /b 1
)
echo Build complete. Run the launcher in the repository root, or desktop\app\ChatAssistant.exe.
pause
