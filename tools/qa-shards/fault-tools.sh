#!/bin/sh
# Q-PW3: the journeys' shards had no libfaketime - P01, P07, F13, N10a, N10c were NOT TESTED in qa-pw-04 although the gate's
# qa job runs them. The same fault tools as the gate (online-backup.yml, "Fault tools"): a machine without them fails here.
for i in 1 2 3; do
  command -v faketime >/dev/null && break
  timeout 300 sudo apt-get install -y faketime || { sleep 15; timeout 300 sudo apt-get update || true; }
done
command -v faketime >/dev/null || { echo "::error::faketime could not be installed after 3 tries: F13 and N10 cannot run"; exit 1; }
faketime -f '+1d' date
sudo -n true || { echo "::error::no passwordless sudo: the small-disk / read-only / private-namespace scenarios cannot run"; exit 1; }
