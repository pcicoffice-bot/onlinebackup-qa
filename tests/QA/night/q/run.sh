#!/bin/bash
# round Q: runs one spec under the machine-wide Playwright lock; output to night/q/<name>.txt
cd "$(dirname "$0")/../.."
spec=$1; out=night/q/$2.txt
QA_NO_BUILD=1 OB_RESTIC=/tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad/restic timeout 2400 \
  flock /tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad/pw.lock npx playwright test "$spec" "${@:3}" --workers=1 --reporter=line > "$out" 2>&1
echo "EXIT $?" >> "$out"
