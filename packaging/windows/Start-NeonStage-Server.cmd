@echo off
setlocal
set "PATH=%~dp0;%PATH%"
if not defined Karaoke__LibraryPath set "Karaoke__LibraryPath=%USERPROFILE%\Music\NeonStage"
if not defined Karaoke__DatabasePath set "Karaoke__DatabasePath=%LOCALAPPDATA%\NeonStage\server\karaoke.db"
if not defined Usdb__CachePath set "Usdb__CachePath=%LOCALAPPDATA%\NeonStage\server\usdb-cache"
set "Karaoke__PublicBaseUrl=http://127.0.0.1:5274"
set "ASPNETCORE_URLS=http://127.0.0.1:5274"
if not defined Online__Enabled set "Online__Enabled=false"
if not defined ASPNETCORE_ENVIRONMENT set "ASPNETCORE_ENVIRONMENT=Production"
if not exist "%USERPROFILE%\Music\NeonStage" mkdir "%USERPROFILE%\Music\NeonStage"
if not exist "%LOCALAPPDATA%\NeonStage\server" mkdir "%LOCALAPPDATA%\NeonStage\server"
if not exist "%LOCALAPPDATA%\NeonStage\server\usdb-cache" mkdir "%LOCALAPPDATA%\NeonStage\server\usdb-cache"
echo Neon Stage Server: %ASPNETCORE_URLS%
"%~dp0Karaoke.Server.exe" %*
