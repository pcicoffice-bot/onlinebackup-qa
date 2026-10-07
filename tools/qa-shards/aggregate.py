#!/usr/bin/env python3
"""QA shards - the aggregator: one verdict from every isolated runner, never a PASS that was not seen.

  aggregate.py --expected expected.json --results DIR --out OUTDIR [--plan PLANDIR] [--mode strict|gate]
               [--build-sha SHA] [--control]

DIR holds one folder per job (shard-<id>/, and control/ for the control run): its .trx file(s) and meta.json.
Per test and job:
  PASS        the job's .trx says Passed and the test did not report "NOT TESTED"
  FAIL        the .trx says Failed (and the failure is not a "NOT TESTED" guard)
  NOT TESTED  - missing: no .trx (job cancelled / did not start / crashed / its machine failed the pre-flight check),
                the job ran another build than the run's (SHA-256), or the test is missing from the .trx
              - declared: skipped, or the test itself said NOT TESTED (a precondition guard)
  DISAPPEARED a known test (tools/qa-shards/known-tests.txt) that is no longer in the build at all
A test is PASS overall only when EVERY expected job of it is PASS.
Exit code - mode strict (QA runs): 0 only when everything is PASS. mode gate (the quality gate keeps its criteria
exactly): 1 on any FAIL, any missing result and any disappeared test; a declared NOT TESTED is listed, as the gate's
log always showed it, and does not change the gate's colour.
With a control run (the previous method): every test's result is compared with the sharded one (SAME / DIFFERENT).
"""
import argparse, glob, json, os, sys, xml.etree.ElementTree as ET
from collections import OrderedDict

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
RANK = {'PASS': 0, 'NOT TESTED': 1, 'FAIL': 2, 'DISAPPEARED': 3}

def seconds(d):
    try:
        h, m, s = d.split(':'); return int(h) * 3600 + int(m) * 60 + float(s)
    except Exception: return 0.0

def read_trx(folder):
    """test name -> (status, seconds, note, kind); None when the job left no .trx at all."""
    files = sorted(glob.glob(os.path.join(folder, '**', '*.trx'), recursive=True))
    if not files: return None
    out = {}
    for f in files:
        try: root = ET.parse(f).getroot()
        except ET.ParseError as e: out['__broken__' + f] = ('NOT TESTED', 0, 'unreadable trx: %s' % e, 'missing'); continue
        for r in root.iter(NS + 'UnitTestResult'):
            name, outcome = r.get('testName'), r.get('outcome')
            text = ' '.join(x.text or '' for x in r.iter() if x.tag in (NS + 'StdOut', NS + 'Message', NS + 'StdErr'))
            msg = ' '.join((x.text or '').strip() for x in r.iter(NS + 'Message'))[:400]
            if outcome == 'Passed':
                v = ('NOT TESTED', 'the test said: ' + text[text.find('NOT TESTED'):][:200].strip(), 'declared') if 'NOT TESTED' in text else ('PASS', '', '')
            elif outcome == 'Failed':
                v = ('NOT TESTED', msg, 'declared') if 'NOT TESTED' in msg else ('FAIL', msg, '')
            else:
                v = ('NOT TESTED', 'outcome ' + str(outcome) + (': ' + msg if msg else ''), 'declared')
            prev = out.get(name)
            if prev is None or RANK[v[0]] > RANK[prev[0]]: out[name] = (v[0], seconds(r.get('duration') or '0:0:0'), v[1], v[2])
    return out

def meta(folder):
    try: return json.load(open(os.path.join(folder, 'meta.json')))
    except Exception: return {}

def load(path, default):
    try: return json.load(open(path))
    except Exception: return default

def main():
    a = argparse.ArgumentParser()
    a.add_argument('--expected', required=True); a.add_argument('--results', required=True); a.add_argument('--out', required=True)
    a.add_argument('--control', action='store_true', help='a control run of the same tests (the previous method) is in control/')
    a.add_argument('--plan', default=''); a.add_argument('--mode', default='strict', choices=['strict', 'gate'])
    a.add_argument('--build-sha', default='')
    a.add_argument('--title', default='QA shards')
    o = a.parse_args()
    expected = json.load(open(o.expected))
    os.makedirs(o.out, exist_ok=True)
    per_test = OrderedDict()   # test -> list of {job, status, sec, note, kind}
    jobs = []
    for jid in sorted(expected):
        folder = os.path.join(o.results, 'shard-' + jid)
        res, m = read_trx(folder), meta(folder)
        pre = str(m.get('preflight', ''))
        if o.build_sha and m.get('build_sha') and m.get('build_sha') != o.build_sha:
            state, res = 'ran another build (%s) - its results do not count' % m.get('build_sha')[:12], None
        elif pre.startswith('failed') or pre.startswith('build'):
            state, res = 'machine check: ' + pre, None
        elif res is None:
            state = 'no results (cancelled, did not start, or crashed before writing)'
        else:
            state = 'results'
        jobs.append({'job': jid, 'state': state, 'meta': m, 'tests': len(expected[jid])})
        for t in expected[jid]:
            if res is None: st, sec, note, kind = 'NOT TESTED', 0, 'job ' + jid + ': ' + state, 'missing'
            else: st, sec, note, kind = res.get(t, ('NOT TESTED', 0, 'job ' + jid + ': not in its results (not run)', 'missing'))
            per_test.setdefault(t, []).append({'job': jid, 'status': st, 'sec': sec, 'note': note, 'kind': kind})
        if res:
            for t in res:
                if t not in expected[jid] and not t.startswith('__broken__'):
                    per_test.setdefault('UNEXPECTED ' + t, []).append({'job': jid, 'status': 'NOT TESTED', 'sec': 0, 'kind': 'missing', 'note': 'ran in ' + jid + ' but not planned there - check the shard filter'})
    for t in load(os.path.join(o.plan, 'disappeared.json'), []) if o.plan else []:
        per_test['DISAPPEARED ' + t] = [{'job': '-', 'status': 'DISAPPEARED', 'sec': 0, 'kind': 'missing',
                                         'note': 'in tools/qa-shards/known-tests.txt but no longer in this build - a test removed on purpose is removed from that file in the same commit'}]
    new = load(os.path.join(o.plan, 'new.json'), []) if o.plan else []
    verdict = {t: max(runs, key=lambda r: RANK[r['status']])['status'] for t, runs in per_test.items()}
    missing = [t for t, runs in per_test.items() if any(r['status'] in ('NOT TESTED', 'DISAPPEARED') and r['kind'] == 'missing' for r in runs)]
    control = None
    if o.control:
        cres = read_trx(os.path.join(o.results, 'control'))
        control = {t: ('NOT TESTED' if cres is None else cres.get(t, ('NOT TESTED', 0, '', ''))[0]) for t in per_test if not t.startswith(('UNEXPECTED ', 'DISAPPEARED '))}
    dur = {}
    for t, runs in per_test.items():
        if t.startswith(('UNEXPECTED ', 'DISAPPEARED ')): continue
        c = t.split('(', 1)[0].rsplit('.', 1)[0]
        best = {}
        for r in runs: best[r['job'].split('-')[0]] = max(best.get(r['job'].split('-')[0], 0), r['sec'])
        dur[c] = round(dur.get(c, 0) + max(best.values() or [0]), 1)
    counts = {k: sum(1 for v in verdict.values() if v == k) for k in ('PASS', 'FAIL', 'NOT TESTED', 'DISAPPEARED')}
    diffs = [t for t in (control or {}) if control[t] != verdict[t]]
    if o.mode == 'gate':
        ok = counts['FAIL'] == 0 and not missing and not diffs and (control is None or 'FAIL' not in control.values())
    else:
        ok = counts['FAIL'] == 0 and counts['NOT TESTED'] == 0 and counts['DISAPPEARED'] == 0 and not diffs and (control is None or all(v == 'PASS' for v in control.values()))
    json.dump({'title': o.title, 'mode': o.mode, 'build_sha': o.build_sha, 'counts': counts, 'verdict': verdict, 'runs': per_test,
               'missing': missing, 'new_tests': new, 'jobs': jobs, 'control': control, 'control_differences': diffs, 'green': ok},
              open(os.path.join(o.out, 'results.json'), 'w'), indent=1)
    json.dump(dur, open(os.path.join(o.out, 'durations.json'), 'w'), indent=1, sort_keys=True)
    L = ['# ' + o.title, '',
         '**%d tests: %d PASS, %d FAIL, %d NOT TESTED%s** (a test is PASS only when every job that had to run it reports PASS)' % (
             len(verdict), counts['PASS'], counts['FAIL'], counts['NOT TESTED'], (', %d DISAPPEARED' % counts['DISAPPEARED']) if counts['DISAPPEARED'] else ''),
         '', 'Build SHA-256: `%s` (every shard checks it before running). Mode: %s - %s.' % (o.build_sha or '-', o.mode,
             'green' if ok else 'NOT green'), '']
    L += ['| job | state | tests | runner | minutes |', '|---|---|---|---|---|']
    for j in jobs:
        m = j['meta']; L.append('| %s | %s | %d | %s | %s |' % (j['job'], j['state'], j['tests'], m.get('runner', '?'), m.get('minutes', '?')))
    reps = max(len(r) for r in per_test.values()) if per_test else 0
    bad = [t for t in verdict if verdict[t] != 'PASS']
    if bad:
        L += ['', '## Not PASS', '', '| test | verdict | per job |', '|---|---|---|']
        for t in bad:
            runs = per_test[t]
            tally = '%d PASS / %d FAIL / %d NOT TESTED of %d' % tuple([sum(1 for r in runs if r['status'] == k) for k in ('PASS', 'FAIL', 'NOT TESTED')] + [len(runs)])
            notes = '; '.join(sorted(set(r['job'] + ': ' + (('[' + r['kind'] + '] ') if r['kind'] else '') + r['note'][:160] for r in runs if r['status'] != 'PASS')))[:700]
            L.append('| %s | %s | %s - %s |' % (t, verdict[t], tally, notes.replace('|', '/').replace('\n', ' ')))
    if new: L += ['', '%d test(s) of this build are not yet in tools/qa-shards/known-tests.txt (add them so that a later disappearance is caught): %s' % (len(new), ', '.join(new[:20]) + (' ...' if len(new) > 20 else ''))]
    if reps > 1:
        L += ['', '## Repetitions', '', '| test | PASS | FAIL | NOT TESTED |', '|---|---|---|---|']
        for t, runs in per_test.items():
            L.append('| %s | %d | %d | %d |' % (t, *[sum(1 for r in runs if r['status'] == k) for k in ('PASS', 'FAIL', 'NOT TESTED')]))
    if control is not None:
        L += ['', '## Control (the previous method) vs shards', '',
              ('**SAME** - every one of %d tests has the same result in the control run and sharded' % len(control)) if not diffs else
              '**DIFFERENT in %d tests** - the sharded run is NOT proven equal to the previous method:' % len(diffs)]
        for t in diffs: L.append('- %s: control %s, sharded %s' % (t, control[t], verdict[t]))
    open(os.path.join(o.out, 'summary.md'), 'w').write('\n'.join(L) + '\n')
    print('\n'.join(L[:5]))
    sys.exit(0 if ok else 1)

if __name__ == '__main__': main()
