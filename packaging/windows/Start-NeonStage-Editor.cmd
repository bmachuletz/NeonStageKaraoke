@echo off
setlocal
set "PATH=%~dp0;%PATH%"
if not defined KARAOKE_SERVER set "KARAOKE_SERVER=http://127.0.0.1:5274"
start "Neon Stage Lyrics Editor" /D "%~dp0" "%~dp0Karaoke.App.Desktop.exe" %*
