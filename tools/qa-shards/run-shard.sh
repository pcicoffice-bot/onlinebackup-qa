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
  # NET48 prototype (mirror branch only): the .NET Framework 4.8 in-process suite
  if [ "${SUITE:-net8}" = net48 ]; then TARGET=tests/Tests.Net48/bin/Debug/net48/Tests.Net48.dll; fi
  sha256sum src/Agent/bin/Debug/*/OnlineBackup.Agent.exe 2>/dev/null | tee "$OUT/agent-exe.sha256"
else
  TARGET=tests/Tests; NOBUILD=--no-build; NOLOGO=-nologo
fi
if ! tools/qa-shards/preflight.sh "$OUT"; then meta "$got" "failed: $(grep FAILED "$OUT/preflight.txt" | tr '\n"' '; ' )" 1 0; exit 1; fi
rc=0
# Flaky Hunter: SERIAL=k runs the same tests k times on THIS machine (state that builds up, a warm machine), each iteration in
# its own folder and counted on its own; LOAD=1 keeps all but one processor busy meanwhile (a contention a quiet runner hides)
SERIAL=${SERIAL:-1}; LOADPID=""
if [ "${LOAD:-0}" = 1 ]; then
  PY=$(command -v python3 || command -v python)
  $PY -c 'import multiprocessing as m, time
def burn():
    while True: pass
n = max(1, m.cpu_count() - 1)
ps = [m.Process(target=burn, daemon=True) for _ in range(n)]
[p.start() for p in ps]; print("load: %d busy processes" % n, flush=True); time.sleep(86400)' > "$OUT/load.txt" 2>&1 &
  LOADPID=$!
fi
if [ "$SERIAL" -gt 1 ]; then
  for k in $(seq 1 "$SERIAL"); do
    IT=$(printf 'i%02d' "$k"); mkdir -p "$OUT/$IT"
    dotnet test $TARGET ${NOBUILD:-} ${NOLOGO:-} --filter "$F" --logger "trx;LogFileName=$ID-$IT.trx" \
      --logger "console;verbosity=normal" --results-directory "$OUT/$IT" > "$OUT/$IT/console.log" 2>&1 || rc=$?
    echo "serial iteration $k of $SERIAL: $(grep -E '^\s+(Passed|Failed): ' "$OUT/$IT/console.log" | tr -s ' ' | tr '\n' ' ')"
  done
  cat "$OUT"/i*/console.log > "$OUT/console.log"
else
  dotnet test $TARGET ${NOBUILD:-} ${NOLOGO:-} --filter "$F" --logger "trx;LogFileName=$ID.trx" \
    --logger "console;verbosity=normal" --results-directory "$OUT" > "$OUT/console.log" 2>&1 || rc=$?
fi
[ -n "$LOADPID" ] && kill "$LOADPID" 2>/dev/null
end=$(date +%s)
# the job log keeps every failure WITH its message and the first lines of its stack (the .trx holds the whole of it)
awk '/^  Failed /{on=1; n=0} /^  (Passed|Skipped) /{on=0} on && n<25 {print; n++}' "$OUT/console.log" | head -n 600
grep -E "^\s+Skipped |^Total tests|^\s+(Passed|Failed|Skipped): |Test Run Aborted" "$OUT/console.log" | head -n 40
meta "$got" "ok" "$rc" "$(( (end - start + 59) / 60 ))"
# The verdict is the aggregator's alone (it reads every test's outcome and message). This job only has to leave results: a
# test that ends "NOT TESTED: <reason>" is Failed for dotnet test, and a job colour taken from that would change the
# gate's criteria. No .trx at all (the test host never started or crashed first) is this job's failure.
if find "$OUT" -name "*.trx" | grep -q .; then exit 0; fi
echo "no results were written (dotnet test exit $rc)"; exit 1
