#!/bin/sh
# Keeps the shards' verdict in the branch qa-evidence (runs/<name>), next to the Windows runs' evidence.
#   keep.sh <folder> <name> <commit>
set -e
SRC=$(cd "$1" && pwd); NAME=$2; SHA=$3; REPO=$(pwd)
W=$(mktemp -d); cd "$W"
git init -q && git remote add origin "$(git -C "$REPO" remote get-url origin)"
git -C "$REPO" config --get-regexp '^http\..*extraheader' | while read k v; do git config "$k" "$v"; done
git config user.name "qa-bot"; git config user.email "qa-bot@users.noreply.github.com"
for i in 1 2 3 4; do
  if git fetch -q --depth 1 origin qa-evidence 2>/dev/null; then git checkout -q -B qa-evidence FETCH_HEAD; else git checkout -q --orphan qa-evidence; fi
  mkdir -p "runs/$NAME" && cp -r "$SRC"/. "runs/$NAME/"
  git add runs && git commit -qm "QA shards verdict $NAME ($SHA)" || true
  git push -q origin qa-evidence && { echo "kept runs/$NAME"; exit 0; }
  sleep $((i * 5))
done
echo "could not push the verdict"; exit 1
