#!/bin/bash
# One shard of the journeys (or the control run): the machine checked first, then its spec files exactly as the serial run
# runs them (workers: 1), Playwright's JSON report turned into a .trx for the aggregator, meta.json. The aggregator decides.
#   ID=<id> FILES="<spec files>" [OWN_BUILD=1: the journeys build the product themselves - the previous method] run-pw.sh
set -u
OUT="$PWD/out/$ID"; mkdir -p "$OUT"
start=$(date +%s)
if ! tools/qa-shards/preflight.sh "$OUT"; then
  printf '{"job":"%s","runner":"%s","preflight":"failed: %s","exit":1,"minutes":0}\n' "$ID" "${RUNNER_NAME:-?}" "$(grep FAILED "$OUT/preflight.txt" | tr '\n"' '; ')" > "$OUT/meta.json"; exit 1
fi
[ "${OWN_BUILD:-0}" = 1 ] || export QA_NO_BUILD=1
rc=0
( cd tests/QA && PLAYWRIGHT_JSON_OUTPUT_NAME="$OUT/report.json" npx playwright test $FILES --reporter=json,list ) > "$OUT/console.log" 2>&1 || rc=$?
end=$(date +%s)
tail -n 30 "$OUT/console.log"
[ -f "$OUT/report.json" ] && python3 tools/qa-shards/pw2trx.py "$OUT/report.json" "$OUT/$ID.trx"
printf '{"job":"%s","runner":"%s","os":"%s","sha":"%s","build_sha":"%s","preflight":"ok","exit":%d,"minutes":%d}\n' "$ID" "${RUNNER_NAME:-?}" \
  "${RUNNER_OS:-?}" "${GITHUB_SHA:-?}" "" "$rc" "$(( (end - start + 59) / 60 ))" > "$OUT/meta.json"
if [ -f "$OUT/$ID.trx" ]; then exit 0; fi
echo "no results were written (playwright exit $rc)"; exit 1
