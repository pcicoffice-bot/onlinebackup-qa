#!/usr/bin/env python3
"""
The numeric verification report — every number from the last runs, never typed by hand:
  python3 tests/QA/report.py [out.md]
Reads: capabilities.py (inventory + three layers, from reports/trx/xunit.trx and reports/last-run.json), the bug log
(docs/R1-BUGLOG.md + the severities below), the static review (tools/static-review-hits.json + triage).
"""
import json, os, re, sys, collections
here = os.path.dirname(os.path.abspath(__file__)); repo = os.path.dirname(os.path.dirname(here))
sys.path.insert(0, here)
import capabilities as C

# Severity of every bug (customer impact): Critical = a failure that looks like success, data not protected, or every
# backup of a customer / the server stops; High = backups blocked, hung or wrong for a while; Medium = a false alarm,
# a wrong record or a delay; Low = cosmetic.
SEVERITY = {
    'Critical': [79, 2, 3, 4, 6, 14, 15, 17, 24, 28, 29, 35, 39, 41, 42, 48, 54, 55, 56],
    'High': [84, 83, 1, 5, 9, 10, 18, 20, 22, 27, 32, 33, 73, 74, 75, 36, 38, 40, 44, 46, 47, 49, 57, 59, 61, 66],
    'Medium': [82, 80, 76, 77, 11, 12, 13, 16, 19, 21, 23, 25, 26, 30, 31, 34, 37, 43, 45, 50, 51, 53, 58, 60, 62, 65, 67, 68, 69, 70, 71],
    'Low': [81, 78, 7, 8, 52, 63, 64, 72],
}

def main(out):
    res = C.results(); rows = C.evaluate(res); comps = C.components(rows); kinds = C.xunit_kinds()
    cnt = lambda pred: sum(1 for r in rows if pred(r))
    crit = [r for r in rows if r['critical']]
    # tests by type, from the last runs
    trx = open(os.path.join(here, 'reports', 'trx', 'xunit.trx'), encoding='utf-8').read()
    outcomes = re.findall(r'<UnitTestResult [^>]*testName="OnlineBackup\.Tests\.\w+\.(\w+)[^"]*"[^>]*outcome="([^"]+)"', trx)
    by = collections.Counter(); byfail = collections.Counter()
    for name, o in outcomes:
        k = kinds.get(name, 'integration'); by[k] += 1
        if o != 'Passed': byfail[k] += 1
    qa = {k: v for k, v in res.items() if k.startswith('qa:')}
    qa_total = len(qa); qa_pass = sum(1 for v in qa.values() if v == 'Passed')
    windows_written = len([f for f in os.listdir(os.path.join(here, 'windows')) if f.endswith('.ps1')]) if os.path.isdir(os.path.join(here, 'windows')) else 0
    # failure scenarios: tests in the failure / recovery dimensions, and the fault-injection E2E runs
    fail_tests = sorted(set(t for r in rows for d in ('failure', 'recovery') if not isinstance(r['dims'][d], C.NA) for t in r['dims'][d]))
    fault_e2e = sorted(k for k in qa if 'failure-recovery' in k or 'j5' in k or 'j7' in k)
    # bugs
    log = open(os.path.join(repo, 'docs', 'R1-BUGLOG.md'), encoding='utf-8').read()
    bugs = sorted(set(int(x) for x in re.findall(r'^\| (\d+) \|', log, re.M)))
    sev = {b: s for s, l in SEVERITY.items() for b in l}
    missing_sev = [b for b in bugs if b not in sev]
    # static review
    hits = json.load(open(os.path.join(repo, 'tools', 'static-review-hits.json')))
    tri = json.load(open(os.path.join(repo, 'tools', 'static-review-triage.json')))
    sr = collections.Counter(tri.get(h['id'], {}).get('verdict', 'UNTRIAGED') for h in hits)
    sr_fixed_records = sum(1 for k, v in tri.items() if v.get('verdict') == 'FIXED' and k not in {h['id'] for h in hits})

    L = []
    w = L.append
    w('# Verification report — numbers from the last runs (`tests/QA/report.py`)')
    w('')
    w('## 1. Capabilities')
    w('')
    w('| | Total | Fully verified (3 layers) | Partial | Not tested | Failing |')
    w('|---|---|---|---|---|---|')
    w('| All capabilities | %d | %d | %d | %d | %d |' % (len(rows), cnt(lambda r: r['status'] == 'FULL'), cnt(lambda r: r['status'] == 'PARTIAL'), cnt(lambda r: r['status'] == 'NONE'), cnt(lambda r: r['status'] == 'FAILING')))
    w('| Critical | %d | %d | %d | %d | %d |' % (len(crit), sum(1 for r in crit if r['status'] == 'FULL'), sum(1 for r in crit if r['status'] == 'PARTIAL'), sum(1 for r in crit if r['status'] == 'NONE'), sum(1 for r in crit if r['status'] == 'FAILING')))
    w('')
    w('| Layer (all / critical) | Verified | Partial | None | Failing |')
    w('|---|---|---|---|---|')
    for Ly in C.LAYERS:
        a = [cnt(lambda r, k=k: r['layers'][Ly]['status'] == k) for k in ('VERIFIED', 'PARTIAL', 'NONE', 'FAILING')]
        b = [sum(1 for r in crit if r['layers'][Ly]['status'] == k) for k in ('VERIFIED', 'PARTIAL', 'NONE', 'FAILING')]
        w('| %s | %d / %d | %d / %d | %d / %d | %d / %d |' % (Ly, a[0], b[0], a[1], b[1], a[2], b[2], a[3], b[3]))
    w('')
    w('Source files: %d, every one mapped to a capability (hidden areas: %d).' % (len(comps), sum(1 for x in comps if not x['caps'])))
    w('')
    w('Fully verified critical capabilities: ' + (', '.join(r['id'] + ' ' + r['name'] for r in crit if r['status'] == 'FULL') or 'none'))
    w('')
    w('## 2. Tests by type (last run)')
    w('')
    w('| Type | Tests | Failed |'); w('|---|---|---|')
    w('| Component (xUnit, no server) | %d | %d |' % (by['component'], byfail['component']))
    w('| Integration (xUnit, real server + HTTP + files) | %d | %d |' % (by['integration'], byfail['integration']))
    w('| End-to-end (Playwright: separate server and agent processes, browser, restore + SHA-256) | %d | %d |' % (qa_total, qa_total - qa_pass))
    w('| Windows (installer robot, Windows E2E) — written, NOT RUN | %d scripts | — |' % windows_written)
    w('')
    w('## 3. Failure scenarios')
    w('')
    w('- Tests in the failure / recovery dimensions of the matrix: **%d**' % len(fail_tests))
    w('- Fault-injection runs on the real programs (E2E): **%d** — %s' % (len(fault_e2e), ', '.join(k.replace('qa:', '') for k in fault_e2e)))
    w('')
    w('## 4. Bugs found and fixed (each with a regression test that failed before the fix)')
    w('')
    w('| Severity | Count | Bugs |'); w('|---|---|---|')
    for s, l in SEVERITY.items():
        l2 = [b for b in l if b in bugs]
        w('| %s | %d | %s |' % (s, len(l2), ', '.join(map(str, l2))))
    w('| **Total** | **%d** | |' % len(bugs))
    if missing_sev: w(''); w('Bugs without a severity: ' + ', '.join(map(str, missing_sev)))
    w('')
    w('Static review: %d hits — OK %d, FIXED %d (+%d fixes recorded by class), BUG open %d, not classified %d.' % (len(hits), sr['OK'], sr['FIXED'], sr_fixed_records, sr['BUG'], sr['UNTRIAGED']))
    w('')
    w('## 5. Risk areas (critical capabilities not fully verified, with what is missing)')
    w('')
    w('| Capability | Component | Integration | End-to-end | Missing |'); w('|---|---|---|---|---|')
    for r in crit:
        if r['status'] == 'FULL': continue
        miss = '; '.join('%s: %s' % (Ly, ', '.join(r['layers'][Ly]['missing'])) for Ly in C.LAYERS if r['layers'][Ly]['status'] != 'VERIFIED')
        w('| %s %s | %s | %s | %s | %s |' % (r['id'], r['name'], *[r['layers'][Ly]['status'] for Ly in C.LAYERS], miss))
    w('')
    w('## 6. Verdict')
    w('')
    full_crit = sum(1 for r in crit if r['status'] == 'FULL')
    w('**NOT READY.** Ready requires every critical capability fully verified in all three layers; %d of %d are. ' % (full_crit, len(crit))
      + 'Failing tests in the last run: %d. The Windows layer (installer, service, VSS, SQL Server, reboot) has not run at all.' % (sum(byfail.values()) + (qa_total - qa_pass)))
    open(out, 'w').write('\n'.join(L) + '\n')
    print(out)

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'reports', 'VERIFICATION-REPORT.md'))
