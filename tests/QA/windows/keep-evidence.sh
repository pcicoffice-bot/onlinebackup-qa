#!/bin/sh
# Keeps one part of a Windows QA run in the branch qa-evidence (runs/<run id>/<part>) and rebuilds that run's contact
# sheet from every part already there (the Windows job and the VM job each add theirs when they end).
#   keep-evidence.sh <folder with the evidence> <part: . for the Windows job, vm for the VM> <run id> <commit>
set -e
SRC=$(cd "$1" && pwd); PART=$2; RUN=$3; SHA=$4; QA=$(cd "$(dirname "$0")" && pwd)
W=$(mktemp -d); cd "$W"
git init -q && git remote add origin "$(git -C "$QA" remote get-url origin)"
git -C "$QA" config --get-regexp '^http\..*extraheader' | while read k v; do git config "$k" "$v"; done
git config user.name "qa-bot"; git config user.email "qa-bot@users.noreply.github.com"
for i in 1 2 3 4; do
  if git fetch -q --depth 1 origin qa-evidence 2>/dev/null; then git checkout -q -B qa-evidence FETCH_HEAD; else git checkout -q --orphan qa-evidence; fi
  mkdir -p "runs/$RUN/$PART" && cp -r "$SRC"/. "runs/$RUN/$PART/"
  python3 "$QA/contact-sheet.py" "runs/$RUN" >/dev/null
  # the 10 newest runs by their run id (a fresh clone gives every folder the checkout time: "ls -t" sorted by luck)
  ls -1 runs | grep -E '^[0-9]+$' | sort -n | head -n -10 | while read d; do [ "$d" = "$RUN" ] || git rm -rfq "runs/$d"; done
  git add runs && git commit -qm "Windows QA evidence, run $RUN ($SHA), part $PART" || true
  git push -q origin qa-evidence && { echo "kept runs/$RUN/$PART"; exit 0; }
  sleep $((i * 5))
done
echo "could not push the evidence"; exit 1
