#!/bin/bash
# restic for one shard, checked against the release's SHA256SUMS (and, on Linux, against the SHA-256 the quality gate pins)
set -euo pipefail
V=${RESTIC_VERSION:-0.18.1}; B=https://github.com/restic/restic/releases/download/v$V
mkdir -p .restic && cd .restic
curl -sSfL -o SHA256SUMS "$B/SHA256SUMS"
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) f=restic_${V}_windows_amd64.zip; win=1;; *) f=restic_${V}_linux_amd64.bz2; win=0;; esac
curl -sSfL -o "$f" "$B/$f"
grep " $f\$" SHA256SUMS | sha256sum -c -
if [ $win = 1 ]; then
  powershell -NoProfile -Command "Expand-Archive -Force '$f' ." && mv -f restic_${V}_windows_amd64.exe restic.exe
  echo "OB_RESTIC=$(cygpath -w "$PWD/restic.exe")" >> "$GITHUB_ENV"
else
  [ "$V" != 0.18.1 ] || echo "680838f19d67151adba227e1570cdd8af12c19cf1735783ed1ba928bc41f363d  $f" | sha256sum -c -
  bunzip2 -c "$f" > restic && chmod 755 restic
  echo "OB_RESTIC=$PWD/restic" >> "$GITHUB_ENV"
fi
