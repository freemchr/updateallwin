#!/usr/bin/env bash
# Mirrors the repo to a Windows folder and runs a Windows dotnet command there.
# (WinUI 3 / WiX tooling only runs on Windows; this lets you build from WSL.)
# Usage: build/wsl-build.sh <dotnet args...>      e.g. build/wsl-build.sh build UpdateAllWin.slnx -c Release
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
WINUSER="$(cmd.exe /c 'echo %USERNAME%' 2>/dev/null | tr -d '\r')"
DEST="/mnt/c/Users/$WINUSER/.updateallwin-build"
mkdir -p "$DEST"
rsync -a --delete --exclude '.git' --exclude 'bin' --exclude 'obj' --exclude 'artifacts' "$REPO/" "$DEST/src-mirror/"
WDEST="C:\\Users\\$WINUSER\\.updateallwin-build\\src-mirror"
cd "$DEST"
powershell.exe -NoProfile -Command "Set-Location '$WDEST'; dotnet $*; exit \$LASTEXITCODE" | tr -d '\r'
