#!/bin/bash
# The machine is checked BEFORE the tests (a broken runner must cost minutes, not the hour of a whole run). It only
# looks: it never repairs what a test is meant to find. A failed check means the shard's tests are NOT TESTED (no
# results are written), never skipped and never PASS. What the machine can do for fault injection is recorded.
#   preflight.sh <out folder>     -> exit 0 and <out>/preflight.txt, or exit 1 with the reason in it
OUT=$1; R="$OUT/preflight.txt"; : > "$R"; bad=0
say() { echo "$1" | tee -a "$R"; }
fail() { say "FAILED: $1"; bad=1; }
PY=$(command -v python3 || command -v python)
dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft.NETCore.App 8\.' && say "ok   .NET 8 runtime" || fail ".NET 8 runtime missing"
if [ -n "${OB_RESTIC:-}" ]; then v=$("$OB_RESTIC" version 2>&1 | head -1); echo "$v" | grep -q "restic ${RESTIC_VERSION:-0.18.1}" && say "ok   $v" || fail "restic: '$v'"; else fail "OB_RESTIC not set"; fi
T=$($PY -c 'import tempfile; print(tempfile.gettempdir())'); f="$T/obpre-$$"
( echo x > "$f" && rm -f "$f" ) 2>/dev/null && say "ok   temp folder writable ($T)" || fail "temp folder not writable ($T)"
free=$($PY -c 'import shutil,sys; print(shutil.disk_usage(sys.argv[1]).free // 2**30)' "$T")
[ "${free:-0}" -ge 2 ] && say "ok   ${free} GB free on the temp volume" || fail "only ${free} GB free on the temp volume (a machine that cannot hold the test data)"
$PY -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); s.listen(1); print(s.getsockname()[1])' >/dev/null 2>&1 && say "ok   loopback ports" || fail "cannot open a loopback port"
# what this machine can do (recorded, not required: the tests that need it say NOT TESTED themselves)
[ "$(id -u 2>/dev/null)" = 0 ] && say "info root: yes" || say "info root: no"
[ -e /dev/full ] && say "info /dev/full: yes" || say "info /dev/full: no"
exit $bad
