#!/bin/bash
# One shard (or the serial control run): its tests exactly as the serial run runs them, a .trx with every test's
# outcome and output, the console log and meta.json - written even when tests fail or the test host crashes.
# Isolation: the job is the only user of its runner (a fresh virtual machine per job), so ports, temp folders,
# repositories and state are its own; the temp folder is deliberately NOT moved (that could change what a test finds).
#   ID=<shard-id> F=<dotnet test filter> run-shard.sh
set -u
OUT="out/$ID"; mkdir -p "$OUT"
start=$(date +%s); rc=0
dotnet test tests/Tests --no-build -nologo --filter "$F" --logger "trx;LogFileName=$ID.trx" \
  --logger "console;verbosity=normal" --results-directory "$OUT" > "$OUT/console.log" 2>&1 || rc=$?
end=$(date +%s)
grep -E "^\s+(Failed|Skipped) |^(Passed|Failed)!|Test Run Aborted|NOT TESTED" "$OUT/console.log" | head -n 80
printf '{"job":"%s","runner":"%s","os":"%s","sha":"%s","exit":%d,"minutes":%d}\n' "$ID" "${RUNNER_NAME:-?}" "${RUNNER_OS:-?}" \
  "${GITHUB_SHA:-?}" "$rc" "$(( (end - start + 59) / 60 ))" > "$OUT/meta.json"
cat "$OUT/meta.json"
exit $rc
