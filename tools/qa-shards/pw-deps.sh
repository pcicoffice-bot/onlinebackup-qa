#!/bin/sh
# Q40: the browser's system packages come from apt, whose mirror sometimes hangs (pw run 37673302054: one shard waited
# 15 min on azure.archive.ubuntu.com and left no results -> 12 journeys NOT TESTED). Shorter tries, retried; a machine that
# still cannot install them fails here loudly (its journeys are NOT TESTED in the verdict, never PASS).
for i in 1 2 3; do
  timeout 420 npx playwright install --with-deps chromium && exit 0
  echo "playwright install try $i failed or hung; trying again"; sleep 20
done
echo "playwright and its system packages could not be installed (3 tries)"; exit 1
