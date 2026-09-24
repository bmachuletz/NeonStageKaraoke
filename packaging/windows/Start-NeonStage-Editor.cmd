@echo off
setlocal
set "PATH=%~dp0;%PATH%"
set "NEONSTAGE_DEFAULT_SERVER_URL=http://127.0.0.1:5274"
start "Neon Stage Lyrics Editor" /D "%~dp0" "%~dp0Karaoke.App.Desktop.exe" %*
