@echo off
cd /d "%~dp0server"
FileManager.Server.exe --urls http://127.0.0.1:5050
if errorlevel 1 (
  echo.
  echo Server stopped with an error. Check whether port 5050 is occupied.
  pause
)
