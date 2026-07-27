#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet restore
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
echo "Fertig: $(pwd)/bin/Release/net10.0/linux-x64/publish"
