@echo off
cd /d "%~dp0client"
FileManager.Client.exe
if errorlevel 1 (
  echo.
  echo Client stopped with an error. Make sure the server was started first.
  pause
)
