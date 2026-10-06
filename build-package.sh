#!/bin/sh
# Builds the installation package the product owner hands to IT companies:
#   OnlineBackup-Server-<version>.zip
#     Setup.cmd (the installation wizard, for anyone), install-server.ps1 (for scripts), README-INSTALL.txt, THIRD-PARTY-NOTICES.txt
#     server\  OnlineBackup.Server.exe (Windows x64, self-contained: no .NET to install)
#     server\client\  the agent (.NET 4.0: Windows 2003 → 2025) + restic.exe — what "create client software" packs
#     server\client\linux\  the Linux x64 agent (self-contained, single file) + restic — the Linux client package
#     server\client\mac\arm64 and mac\x64\  the macOS agents (Apple silicon / Intel) + restic — the Mac client package
# restic is the official release, checked against its published SHA-256 before it is packed.
set -e
cd "$(dirname "$0")"
VERSION=${VERSION:-$(date -u +%Y.%m.%d)}
RESTIC_VERSION=0.18.1
RESTIC_SHA=0c1a713440578cb400d2e76208feb24f1b339426b075a21f73b6b2132692515d
RESTIC_LINUX_SHA=680838f19d67151adba227e1570cdd8af12c19cf1735783ed1ba928bc41f363d
RESTIC_MAC_ARM64_SHA=193fccc8bb4567b498923bc70261e104ff22be88016f0f108b035dad372ab711
RESTIC_MAC_X64_SHA=eb8543ed92ff1ddb67762daebf09f7bea4b0c37d21edb6a910bee3d4f514015f
OUT=${OUT:-out}
rm -rf "$OUT/pkg" && mkdir -p "$OUT/pkg/server/client/linux"
dotnet publish src/Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT/pkg/server" -nologo -v q
dotnet build src/Agent -c Release -f net40 -o "$OUT/agent" -nologo -v q
cp "$OUT/agent/OnlineBackup.Agent.exe" "$OUT/agent/OnlineBackup.Agent.exe.config" "$OUT/agent/OnlineBackup.Core.dll" "$OUT/agent/BouncyCastle.Crypto.dll" "$OUT/pkg/server/client/"
# SETUP-C40: Setup.exe — the client's installation program (a window, asks for administrator rights)
dotnet build src/Setup -c Release -o "$OUT/setup" -nologo -v q
cp "$OUT/setup/Setup.exe" "$OUT/setup/Setup.exe.config" "$OUT/pkg/server/client/"
# CLI-100: OnlineBackup.Client.exe — the customer's program window (and the icon by the clock)
dotnet build src/ClientApp -c Release -o "$OUT/clientapp" -nologo -v q
cp "$OUT/clientapp/OnlineBackup.Client.exe" "$OUT/clientapp/OnlineBackup.Client.exe.config" "$OUT/pkg/server/client/"
if [ ! -f "$OUT/restic_win.zip" ]; then curl -sSL -o "$OUT/restic_win.zip" "https://github.com/restic/restic/releases/download/v$RESTIC_VERSION/restic_${RESTIC_VERSION}_windows_amd64.zip"; fi
echo "$RESTIC_SHA  $OUT/restic_win.zip" | sha256sum -c -
unzip -p "$OUT/restic_win.zip" "restic_${RESTIC_VERSION}_windows_amd64.exe" > "$OUT/pkg/server/client/restic.exe"
cp THIRD-PARTY-NOTICES.txt "$OUT/pkg/server/client/"
dotnet publish src/Agent -c Release -f net8.0 -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -o "$OUT/agent-linux" -nologo -v q
cp "$OUT/agent-linux/OnlineBackup.Agent" "$OUT/pkg/server/client/linux/"
if [ ! -f "$OUT/restic_linux.bz2" ]; then curl -sSL -o "$OUT/restic_linux.bz2" "https://github.com/restic/restic/releases/download/v$RESTIC_VERSION/restic_${RESTIC_VERSION}_linux_amd64.bz2"; fi
echo "$RESTIC_LINUX_SHA  $OUT/restic_linux.bz2" | sha256sum -c -
bunzip2 -c "$OUT/restic_linux.bz2" > "$OUT/pkg/server/client/linux/restic"
chmod 755 "$OUT/pkg/server/client/linux/OnlineBackup.Agent" "$OUT/pkg/server/client/linux/restic"
# PKG-070: macOS, both processors (the setup picks the right one)
for M in "arm64:arm64:$RESTIC_MAC_ARM64_SHA" "x64:amd64:$RESTIC_MAC_X64_SHA"; do
  A=${M%%:*}; R=${M#*:}; SHA=${R#*:}; R=${R%%:*}
  mkdir -p "$OUT/pkg/server/client/mac/$A"
  dotnet publish src/Agent -c Release -f net8.0 -r "osx-$A" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:InvariantGlobalization=true -o "$OUT/agent-mac-$A" -nologo -v q
  cp "$OUT/agent-mac-$A/OnlineBackup.Agent" "$OUT/pkg/server/client/mac/$A/"
  if [ ! -f "$OUT/restic_mac_$A.bz2" ]; then curl -sSL -o "$OUT/restic_mac_$A.bz2" "https://github.com/restic/restic/releases/download/v$RESTIC_VERSION/restic_${RESTIC_VERSION}_darwin_$R.bz2"; fi
  echo "$SHA  $OUT/restic_mac_$A.bz2" | sha256sum -c -
  bunzip2 -c "$OUT/restic_mac_$A.bz2" > "$OUT/pkg/server/client/mac/$A/restic"
  chmod 755 "$OUT/pkg/server/client/mac/$A/OnlineBackup.Agent" "$OUT/pkg/server/client/mac/$A/restic"
done
rm -f "$OUT"/pkg/server/*.pdb
cp install-server.ps1 THIRD-PARTY-NOTICES.txt "$OUT/pkg/"
# SETUP-001: double-click Setup.cmd — it asks for administrator rights and opens the installation wizard in the browser
printf '@echo off\r\nrem Backup server installation - double-click this file\r\nnet session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -FilePath \x27%%~f0\x27 -Verb RunAs" & exit /b)\r\ncd /d "%%~dp0"\r\n"%%~dp0server\\OnlineBackup.Server.exe" setup\r\n' > "$OUT/pkg/Setup.cmd"
cp docs/README-INSTALL.txt "$OUT/pkg/README-INSTALL.txt"
echo "$VERSION" > "$OUT/pkg/version.txt"
echo "$VERSION" > "$OUT/pkg/server/version.txt"
echo "$VERSION" > "$OUT/pkg/server/client/version.txt"   # UPD-020: the client knows its version (Update in the program)   # UPD-010: the server knows its own version (the Update button)
( cd "$OUT/pkg" && rm -f "../OnlineBackup-Server-$VERSION.zip" && zip -9 -qr "../OnlineBackup-Server-$VERSION.zip" . )
sha256sum "$OUT/OnlineBackup-Server-$VERSION.zip"
