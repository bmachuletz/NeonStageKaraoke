NEON STAGE FOR WINDOWS
======================

Server
------
Start-NeonStage-Server.cmd starts the local-only server on 127.0.0.1:5274.
LiveKit is disabled by default. The package uses:

  Library:  %USERPROFILE%\Music\NeonStage
  Database: %LOCALAPPDATA%\NeonStage\server\karaoke.db
  Cache:    %LOCALAPPDATA%\NeonStage\server\usdb-cache

The single-EXE package keeps its server data below
%LOCALAPPDATA%\NeonStage\standalone-server. Use the regular server deployment
instead when the service must be reachable from the LAN or Internet.

Lyrics Editor
-------------
Start-NeonStage-Editor.cmd and the portable EXE connect to
http://127.0.0.1:5274. Starting Karaoke.App.Desktop.exe directly uses the
server address saved in the editor settings.

Stage
-----
Start-NeonStage-Stage.cmd and the portable EXE connect to
http://127.0.0.1:5274. Starting NeonStage.exe directly allows another server
to be selected in the Stage settings.

The release contains both ZIP packages and AppImage-like single EXE launchers.
The launchers extract their versioned build payload below
%LOCALAPPDATA%\NeonStage\portable and start it from there. ZIP users must keep
every file within its extracted directory and must not run it inside the ZIP.
