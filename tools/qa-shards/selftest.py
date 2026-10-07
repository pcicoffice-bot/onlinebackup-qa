#!/usr/bin/env python3
"""Self-test of plan.py and aggregate.py on made-up results: every way a shard can fail to prove something must end
NOT TESTED or FAIL, never PASS. Runs in CI before any shard is planned (python3 tools/qa-shards/selftest.py)."""
import json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'

def trx(path, results):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    rows = ''
    for name, outcome, out, msg in results:
        rows += '<UnitTestResult testName="%s" outcome="%s" duration="00:00:02.5"><Output>%s%s</Output></UnitTestResult>' % (
            name.replace('"', '&quot;'), outcome, '<StdOut>%s</StdOut>' % out if out else '',
            '<ErrorInfo><Message>%s</Message></ErrorInfo>' % msg if msg else '')
    open(path, 'w').write('<?xml version="1.0"?><TestRun xmlns="%s"><Results>%s</Results></TestRun>' % (NS, rows))

def run(*a):
    p = subprocess.run([sys.executable] + list(a), capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr

def check(cond, what):
    if not cond: print('SELFTEST FAIL: ' + what); sys.exit(1)
    print('ok  ' + what)

d = tempfile.mkdtemp()
lst = os.path.join(d, 'list.txt')
open(lst, 'w').write('The following Tests are available:\n' + ''.join('    N.%s.%s\n' % (c, m) for c in ('A', 'B', 'C', 'AB') for m in ('x', 'y')) + '    N.C.t(kind: "pre")\n')
rc, out = run(os.path.join(HERE, 'plan.py'), '--tests', lst, '--shards', '3', '--repeat', '2', '--out', d)
check(rc == 0, 'plan runs')
m = json.load(open(os.path.join(d, 'matrix.json')))['include']; e = json.load(open(os.path.join(d, 'expected.json')))
check(len(m) == 6, '3 shards x 2 repetitions = 6 jobs')
allt = sorted(set(t for v in e.values() for t in v))
check(len(allt) == 9, 'every listed test is planned')
check(all(sorted(set(t for j, v in e.items() if j.endswith('-r01') for t in v)) == allt for _ in [0]), 'repetition 1 covers every test once')
check(all(c.endswith('.') for j in m for c in [x.split('~', 1)[1].rstrip(')') for x in j['filter'].split('|')]), 'every class filter ends with a dot (N.A. never selects N.AB)')
rc, _ = run(os.path.join(HERE, 'plan.py'), '--tests', os.path.join(d, 'nothing'), '--out', d) if open(os.path.join(d, 'nothing'), 'w').write('') == 0 else (1, '')
check(rc != 0, 'an empty test list is refused, not a green plan')

# results: s01 all pass (both reps); s02-r01 one FAIL, one NOT TESTED guard; s02-r02 no trx at all (cancelled);
# s03-r01 one test missing (host crash), s03-r02 one skipped
res = os.path.join(d, 'res')
def jobs_of(s): return [j for j in e if j.startswith(s)]
for j in jobs_of('s01'): trx(os.path.join(res, 'shard-' + j, 'r.trx'), [(t, 'Passed', 'fine', '') for t in e[j]])
j = 's02-r01'; ts = e[j]
trx(os.path.join(res, 'shard-' + j, 'r.trx'), [(ts[0], 'Failed', '', 'Assert.Equal() Failure'), (ts[1], 'Passed', 'NOT TESTED: OB_RESTIC not set', '')] + [(t, 'Passed', '', '') for t in ts[2:]])
os.makedirs(os.path.join(res, 'shard-s02-r02'), exist_ok=True)
j = 's03-r01'; ts = e[j]
trx(os.path.join(res, 'shard-' + j, 'r.trx'), [(t, 'Passed', '', '') for t in ts[1:]])
j = 's03-r02'; ts = e[j]
trx(os.path.join(res, 'shard-' + j, 'r.trx'), [(ts[0], 'NotExecuted', '', 'skipped'), ] + [(t, 'Passed', '', '') for t in ts[1:]])
rc, out = run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'expected.json'), '--results', res, '--out', os.path.join(d, 'agg'))
r = json.load(open(os.path.join(d, 'agg', 'results.json')))
check(rc != 0, 'anything not PASS -> the aggregate is not green')
for t in e['s01-r01']: check(r['verdict'][t] == 'PASS', 'seen PASS in both repetitions -> PASS: ' + t)
s2 = e['s02-r01']
check(r['verdict'][s2[0]] == 'FAIL', 'a failure in one repetition -> FAIL')
check(r['verdict'][s2[1]] in ('NOT TESTED', 'FAIL'), 'a test that said NOT TESTED is never PASS')
check(all(r['verdict'][t] != 'PASS' for t in s2), 'a cancelled repetition (no trx) makes its tests NOT TESTED, never PASS')
s3 = e['s03-r01']
check(r['verdict'][s3[0]] == 'NOT TESTED', 'a test missing from the results (crash) and skipped -> NOT TESTED')
# a job folder missing completely
os.rename(os.path.join(res, 'shard-s01-r02'), os.path.join(d, 'gone'))
rc, out = run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'expected.json'), '--results', res, '--out', os.path.join(d, 'agg2'))
r = json.load(open(os.path.join(d, 'agg2', 'results.json')))
check(all(r['verdict'][t] == 'NOT TESTED' for t in e['s01-r02']), 'a job that left nothing at all -> NOT TESTED')
# control: all green both ways -> SAME and exit 0
res3 = os.path.join(d, 'res3'); one = {k: v for k, v in e.items() if k.endswith('-r01')}
json.dump(one, open(os.path.join(d, 'one.json'), 'w'))
for j in one: trx(os.path.join(res3, 'shard-' + j, 'r.trx'), [(t, 'Passed', '', '') for t in one[j]])
trx(os.path.join(res3, 'control', 'c.trx'), [(t, 'Passed', '', '') for t in allt])
rc, out = run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'one.json'), '--results', res3, '--out', os.path.join(d, 'agg3'), '--control')
check(rc == 0 and 'SAME' in open(os.path.join(d, 'agg3', 'summary.md')).read(), 'serial control equal to shards -> SAME, green')
trx(os.path.join(res3, 'control', 'c.trx'), [(t, 'Failed' if t == allt[0] else 'Passed', '', 'boom') for t in allt])
rc, out = run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'one.json'), '--results', res3, '--out', os.path.join(d, 'agg4'), '--control')
check(rc != 0 and 'DIFFERENT in 1' in open(os.path.join(d, 'agg4', 'summary.md')).read(), 'a difference between serial and shards is reported and not green')
# a part of a class: exact method filters; a theory's every case is expected
rc, _ = run(os.path.join(HERE, 'plan.py'), '--tests', lst, '--shards', '4', '--repeat', '15', '--select', 'N.C.t(kind: "pre") N.A.x', '--out', os.path.join(d, 'p2'))
m2 = json.load(open(os.path.join(d, 'p2', 'matrix.json')))['include']; e2 = json.load(open(os.path.join(d, 'p2', 'expected.json')))
check(rc == 0 and len(m2) == 30, 'two classes x 15 repetitions = 30 jobs (shards never more than classes)')
check(all(set(j['filter'].split('|')) <= {'FullyQualifiedName=N.A.x', 'FullyQualifiedName=N.C.t'} for j in m2), 'part of a class -> exact method filters only')
check(sorted(set(t for v in e2.values() for t in v)) == ['N.A.x', 'N.C.t(kind: "pre")'], 'exactly the selected tests are expected')
# --- build once, machine check, disappeared tests, gate mode ---
res5 = os.path.join(d, 'res5'); one2 = {k: v for k, v in e.items() if k.endswith('-r01')}
json.dump(one2, open(os.path.join(d, 'one2.json'), 'w'))
def meta5(j, **kw):
    os.makedirs(os.path.join(res5, 'shard-' + j), exist_ok=True); json.dump(dict({'build_sha': 'aaa', 'preflight': 'ok'}, **kw), open(os.path.join(res5, 'shard-' + j, 'meta.json'), 'w'))
j1, j2, j3 = sorted(one2)
trx(os.path.join(res5, 'shard-' + j1, 'r.trx'), [(t, 'Passed', 'NOT TESTED: guard' if i == 0 else '', '') for i, t in enumerate(one2[j1])]); meta5(j1)
trx(os.path.join(res5, 'shard-' + j2, 'r.trx'), [(t, 'Passed', '', '') for t in one2[j2]]); meta5(j2, build_sha='bbb')
meta5(j3, preflight='failed: only 1 GB free')
agg = lambda name, *extra: run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'one2.json'), '--results', res5, '--out', os.path.join(d, name), '--build-sha', 'aaa', *extra)
rc, _ = agg('a5'); r = json.load(open(os.path.join(d, 'a5', 'results.json')))
check(all(r['verdict'][t] == 'NOT TESTED' for t in one2[j2]), 'a job that ran ANOTHER build (SHA-256) -> its tests NOT TESTED, even though they passed')
check(all(r['verdict'][t] == 'NOT TESTED' for t in one2[j3]), 'a machine that failed the pre-flight check -> NOT TESTED, never skipped or PASS')
check(rc != 0, 'strict: not green')
# gate mode keeps the gate's criteria: a declared NOT TESTED does not change the colour; a missing result does
os.makedirs(os.path.join(d, 'p6'), exist_ok=True); json.dump([], open(os.path.join(d, 'p6', 'disappeared.json'), 'w'))
res6 = os.path.join(d, 'res6')
for j in one2:
    trx(os.path.join(res6, 'shard-' + j, 'r.trx'), [(t, 'Passed', 'NOT TESTED: guard' if (j == j1 and i == 0) else '', '') for i, t in enumerate(one2[j])])
    os.makedirs(os.path.join(res6, 'shard-' + j), exist_ok=True); json.dump({'build_sha': 'aaa', 'preflight': 'ok'}, open(os.path.join(res6, 'shard-' + j, 'meta.json'), 'w'))
g = lambda name: run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'one2.json'), '--results', res6, '--out', os.path.join(d, name), '--build-sha', 'aaa', '--mode', 'gate', '--plan', os.path.join(d, 'p6'))[0]
check(g('g1') == 0, 'gate: every test passed, one said NOT TESTED itself -> green, as the gate always was (listed, not hidden)')
check('NOT TESTED' in open(os.path.join(d, 'g1', 'summary.md')).read(), 'gate: the declared NOT TESTED is in the summary')
check(run(os.path.join(HERE, 'aggregate.py'), '--expected', os.path.join(d, 'one2.json'), '--results', res6, '--out', os.path.join(d, 's1'), '--build-sha', 'aaa', '--plan', os.path.join(d, 'p6'))[0] != 0, 'strict: the same results are NOT green (NOT TESTED is never PASS)')
import shutil; shutil.rmtree(os.path.join(res6, 'shard-' + j3))
check(g('g2') != 0, 'gate: a shard that left no results -> red')
trx(os.path.join(res6, 'shard-' + j3, 'r.trx'), [(t, 'Passed', '', '') for t in one2[j3]])
json.dump(['N.Gone.test'], open(os.path.join(d, 'p6', 'disappeared.json'), 'w'))
check(g('g3') != 0, 'gate: a known test that disappeared from the build -> red')
r = json.load(open(os.path.join(d, 'g3', 'results.json'))); check(r['verdict'].get('DISAPPEARED N.Gone.test') == 'DISAPPEARED', 'the disappeared test is named')
json.dump([], open(os.path.join(d, 'p6', 'disappeared.json'), 'w'))
trx(os.path.join(res6, 'shard-' + j3, 'r.trx'), [(t, 'Failed', '', 'boom') for t in one2[j3]])
check(g('g4') != 0, 'gate: a failed test -> red')
# plan: --known finds a test that is gone and the new ones
open(os.path.join(d, 'known.txt'), 'w').write('# known\nN.A.x\nN.Gone.test\nN.Other.z\n')
rc, _ = run(os.path.join(HERE, 'plan.py'), '--tests', lst, '--shards', '2', '--select', 'N.A. N.Gone', '--known', os.path.join(d, 'known.txt'), '--out', os.path.join(d, 'p7'))
check(rc == 0 and json.load(open(os.path.join(d, 'p7', 'disappeared.json'))) == ['N.Gone.test'], 'plan: a known test of the selection that is gone is reported (one outside the selection is not)')
check(json.load(open(os.path.join(d, 'p7', 'new.json'))) == ['N.A.y'], 'plan: a test not yet known is reported as new')
print('SELFTEST PASS')
