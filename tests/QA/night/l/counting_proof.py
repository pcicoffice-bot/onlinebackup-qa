#!/usr/bin/env python3
"""QA agent L: proofs that the counting in tests/QA/capabilities.py (used by ledger.py and report.py) can say PASS / VERIFIED
for things that did not really run. Read-only on the repository: every synthetic input is written to a temporary copy.
  python3 -I tests/QA/night/l/counting_proof.py
Each case prints PROVEN (the weakness is real) or NOT PROVEN."""
import json, os, re, shutil, subprocess, sys, tempfile
here = os.path.dirname(os.path.abspath(__file__)); qa = os.path.dirname(os.path.dirname(here)); tests = os.path.join(os.path.dirname(qa), 'Tests')

def copy_qa(trx=None, lastrun=None):
    d = tempfile.mkdtemp(prefix='ql-count-'); q = os.path.join(d, 'tests', 'QA'); os.makedirs(os.path.join(q, 'reports', 'trx'))
    for f in ('capabilities.py', 'specs.py'): shutil.copy(os.path.join(qa, f), q)
    shutil.copytree(tests, os.path.join(d, 'tests', 'Tests'), ignore=shutil.ignore_patterns('bin', 'obj'))
    if trx is not None: open(os.path.join(q, 'reports', 'trx', 'xunit.trx'), 'w').write(trx)
    if lastrun is not None: json.dump(lastrun, open(os.path.join(q, 'reports', 'last-run.json'), 'w'))
    return d, q

def evaluate(q, extra_env=None, code='import capabilities as C, json; r=C.results(); print(json.dumps({"res": r, "rows": [dict(id=x["id"], status=x["status"], layers={L: x["layers"][L]["status"] for L in C.LAYERS}) for x in C.evaluate(r)]}))'):
    env = dict(os.environ); env.update(extra_env or {})
    out = subprocess.run([sys.executable, '-c', code], cwd=q, env=env, capture_output=True, text=True)
    if out.returncode != 0: raise RuntimeError(out.stderr)
    return json.loads(out.stdout)

def trx(results):   # [(Class.Method, outcome, duration)]
    rows = ''.join('<UnitTestResult testId="%d" testName="OnlineBackup.Tests.%s" computerName="x" duration="%s" outcome="%s" />\n' % (i, n, d, o) for i, (n, o, d) in enumerate(results))
    return '<TestRun><Results>\n' + rows + '</Results></TestRun>'

ok = True
# ---- Case 1: a test that returned at its first line (guard) is "Passed" in the trx and counts as a verified layer.
real = open(os.path.join(qa, 'reports', 'trx', 'xunit.trx'), encoding='utf-8').read()
fast = {m.group(1) + '.' + m.group(2): m.group(3) for m in re.finditer(r'testName="OnlineBackup\.Tests\.(\w+)\.(\w+)[^"]*"[^>]*duration="([^"]+)"[^>]*outcome="Passed"', real)}
guards = subprocess.run([sys.executable, '-I', os.path.join(here, 'guards.py')], capture_output=True, text=True).stdout.split('\n')
guarded = {l.split('\t')[0].split(':')[0].replace('.cs', '') + '.' + l.split('\t')[1]: l.split('\t')[2] for l in guards if l.strip()}
noop = sorted(k for k, d in fast.items() if k in guarded and d < '00:00:00.0100000')
print('Case 1 — guarded tests that "Passed" in under 10 ms (a fast pass is not proof of a no-op: read the guard) in the committed trx (tests/QA/reports/trx/xunit.trx):')
for k in noop: print('   ', fast[k], k, '   guard: if (' + guarded[k] + ') return;')
d, q = copy_qa(trx=real)
r = evaluate(q)
ui9 = [x for x in r['rows'] if x['id'] == 'UI-09'][0]
print('    UI-09 (500 customers stay fast) integration layer:', ui9['layers']['integration'], '— its only test is LoadTests.BigServer_..., which returns at once without OB_LOAD=1')
c1 = ui9['layers']['integration'] == 'VERIFIED' and any('LoadTests' in k for k in noop)
print('Case 1:', 'PROVEN' if c1 else 'NOT PROVEN'); ok &= c1; shutil.rmtree(d)

# ---- Case 2: two tests in one spec file: the first FAILED, the second passed -> the file key says Passed.
lr = {'suites': [{'file': 'journeys/j3-backup-now.spec.ts', 'specs': [
        {'title': 'first', 'ok': False, 'tests': [{'results': [{'status': 'failed'}]}]},
        {'title': 'second', 'ok': True, 'tests': [{'results': [{'status': 'passed'}]}]}]}]}
d, q = copy_qa(trx=trx([]), lastrun=lr)
r = evaluate(q)
c2 = r['res'].get('qa:journeys/j3-backup-now') == 'Passed'
print('Case 2 — a spec file with a failed test then a passed one is recorded as:', r['res'].get('qa:journeys/j3-backup-now'), '->', 'PROVEN' if c2 else 'NOT PROVEN')
ok &= c2; shutil.rmtree(d)

# ---- Case 3: QA_EXTRA_RESULTS (an environment variable) marks anything Passed without a run.
d, q = copy_qa(trx=trx([]))
r0 = evaluate(q); r1 = evaluate(q, {'QA_EXTRA_RESULTS': json.dumps({'qa:journeys/j4': 'Passed', 'win:W05': 'Passed'})})
b0 = [x for x in r0['rows'] if x['id'] == 'RS-01'][0]['layers']['e2e']; b1 = [x for x in r1['rows'] if x['id'] == 'RS-01'][0]['layers']['e2e']
c3 = b0 != 'VERIFIED' and b1 == 'VERIFIED'
print('Case 3 — RS-01 end-to-end layer with no run at all:', b0, '; with QA_EXTRA_RESULTS set:', b1, '->', 'PROVEN' if c3 else 'NOT PROVEN')
ok &= c3; shutil.rmtree(d)

# ---- Case 4: the "restore + SHA-256" credit is a prefix match: f1 also credits f10..f19.
sys.path.insert(0, qa); import capabilities as C
t = 'qa:failure-recovery/f13'
c4 = any(t.startswith(x) for x in C.E2E_DATA_CHECK) and t not in C.E2E_DATA_CHECK
print('Case 4 — %s gets the restore+SHA-256 credit through the prefix of qa:failure-recovery/f1:' % t, 'PROVEN' if c4 else 'NOT PROVEN')
ok &= c4
# ---- Case 5: a test id whose class does not have that method still resolves (by method name in any class).
res = {'x:*.JsonReadsAndWritesGraphShapes': 'Passed', 'x:M365Tests.JsonReadsAndWritesGraphShapes': 'Passed'}
c5 = C.resolve('x:AiTests.JsonReadsAndWritesGraphShapes', res) == 'Passed'
print('Case 5 — x:AiTests.JsonReadsAndWritesGraphShapes (the method is in M365Tests) resolves to:', C.resolve('x:AiTests.JsonReadsAndWritesGraphShapes', res), '->', 'PROVEN' if c5 else 'NOT PROVEN')
ok &= c5
print('ALL PROVEN' if ok else 'SOME NOT PROVEN')
