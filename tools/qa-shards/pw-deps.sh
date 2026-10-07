#!/bin/sh
# Q40: the browser's system packages come from apt, whose mirror sometimes hangs (pw run 37673302054: one shard waited
# 15 min on azure.archive.ubuntu.com and left no results -> 12 journeys NOT TESTED). Shorter tries, retried; a machine that
# still cannot install them fails here loudly (its journeys are NOT TESTED in the verdict, never PASS).
# Q40c: timeout stops only npx - the apt-get it started through sudo kept running and held the dpkg lock, so tries 2 and 3
# failed at once on "Could not get lock" (gate 37697279080, website). A hung try's apt is stopped and the lock waited for.
free_apt() {
  sudo -n pkill -TERM -x apt-get 2>/dev/null; sudo -n pkill -TERM -x dpkg 2>/dev/null; sleep 5
  sudo -n pkill -KILL -x apt-get 2>/dev/null; sudo -n pkill -KILL -x dpkg 2>/dev/null
  n=0; while sudo -n fuser /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock >/dev/null 2>&1 && [ $n -lt 60 ]; do sleep 2; n=$((n+2)); done
  sudo -n dpkg --configure -a >/dev/null 2>&1 || true
}
for i in 1 2 3; do
  timeout -k 15 420 npx playwright install --with-deps chromium && exit 0
  echo "playwright install try $i failed or hung; stopping its apt and trying again"; free_apt; sleep 20
done
echo "playwright and its system packages could not be installed (3 tries)"; exit 1
