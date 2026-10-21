@echo off
if not exist "%~dp0desktop\app\ChatAssistant.exe" (
  echo Please build the desktop app first. See desktop\README.md.
  pause
  exit /b 1
)
start "" "%~dp0desktop\app\ChatAssistant.exe"
