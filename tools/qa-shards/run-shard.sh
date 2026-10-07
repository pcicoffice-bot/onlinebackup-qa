#!/bin/bash
# One shard (or the serial control run): its tests exactly as the serial run runs them, a .trx with every test's
# outcome and output, the console log and meta.json - written even when tests fail or the test host crashes.
# Isolation: the job is the only user of its runner (a fresh virtual machine per job), so ports, temp folders,
# repositories and state are its own; the temp folder is deliberately NOT moved (that could change what a test finds).
#   ID=<id> F=<dotnet test filter> [BUILD=<folder with build.tar + build.sha256> BUILD_SHA=<expected>] run-shard.sh
# With BUILD the shard runs the ONE build of the run (its SHA-256 checked first); without it (the control run) the build
# made in this job from the source.
set -u
OUT="out/$ID"; mkdir -p "$OUT"
meta() { printf '{"job":"%s","runner":"%s","os":"%s","sha":"%s","build_sha":"%s","preflight":"%s","exit":%d,"minutes":%d}\n' "$ID" \
  "${RUNNER_NAME:-?}" "${RUNNER_OS:-?}" "${GITHUB_SHA:-?}" "$1" "$2" "$3" "$4" > "$OUT/meta.json"; cat "$OUT/meta.json"; }
start=$(date +%s); got=""
if [ -n "${BUILD:-}" ]; then
  got=$(cd "$BUILD" && sha256sum build.tar | cut -d' ' -f1)
  if [ "$got" != "${BUILD_SHA:-}" ]; then echo "the build is not the run's build: $got != ${BUILD_SHA:-}"; meta "$got" "build mismatch" 1 0; exit 1; fi
  tar -xf "$BUILD/build.tar" || { meta "$got" "build could not be unpacked" 1 0; exit 1; }
  TARGET=tests/Tests/bin/Debug/net8.0/Tests.dll; NOLOGO=
else
  TARGET=tests/Tests; NOBUILD=--no-build; NOLOGO=-nologo
fi
if ! tools/qa-shards/preflight.sh "$OUT"; then meta "$got" "failed: $(grep FAILED "$OUT/preflight.txt" | tr '\n"' '; ' )" 1 0; exit 1; fi
rc=0
dotnet test $TARGET ${NOBUILD:-} ${NOLOGO:-} --filter "$F" --logger "trx;LogFileName=$ID.trx" \
  --logger "console;verbosity=normal" --results-directory "$OUT" > "$OUT/console.log" 2>&1 || rc=$?
end=$(date +%s)
# the job log keeps every failure WITH its message and the first lines of its stack (the .trx holds the whole of it)
awk '/^  Failed /{on=1; n=0} /^  (Passed|Skipped) /{on=0} on && n<25 {print; n++}' "$OUT/console.log" | head -n 600
grep -E "^\s+Skipped |^Total tests|^\s+(Passed|Failed|Skipped): |Test Run Aborted" "$OUT/console.log" | head -n 40
meta "$got" "ok" "$rc" "$(( (end - start + 59) / 60 ))"
exit $rc
