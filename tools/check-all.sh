#!/bin/bash
# QA-010: the same gate as CI, on this computer, before pushing: tests → screens in every language → the website.
# Stops at the first failure. Needs OB_RESTIC (the restic program) and Playwright with Chromium.
#   OB_RESTIC=/path/restic tools/check-all.sh
set -e
cd "$(dirname "$0")/.."
export NODE_PATH=${NODE_PATH:-$(npm root -g)}
echo "== 1/3 tests"; ./run-tests.sh
echo "== 2/3 screens (all languages)"; OUT=${OUT:-ui-report} tools/ui-check/run.sh
echo "== 3/3 website"; node tools/site/build.mjs site/dist && node tools/site/check.mjs site/dist site-report
echo "QUALITY GATE OK"
