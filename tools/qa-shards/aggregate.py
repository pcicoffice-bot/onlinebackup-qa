#!/usr/bin/env python3
"""QA shards - the aggregator: one verdict from every isolated runner, never a PASS that was not seen.

  aggregate.py --expected expected.json --results DIR --out OUTDIR [--control]

DIR holds one folder per job (shard-<id>/, and control/ for the serial control run): its .trx file(s) and meta.json.
Per test and job:
  PASS        the job's .trx says Passed and the test did not report "NOT TESTED"
  FAIL        the .trx says Failed (and the failure is not a "NOT TESTED" guard)
  NOT TESTED  no .trx (job cancelled / did not start / crashed before writing), the test missing from the .trx (host
              crash, filter mismatch), skipped, or the test itself said NOT TESTED (a precondition guard)
A test is PASS overall only when EVERY expected job of it is PASS. The exit code is 0 only when everything is PASS.
With a control run: the serial result of every test is compared with the sharded one - a difference is reported
(the proof that splitting the run does not change what the tests find).
"""
import argparse, glob, json, os, sys, xml.etree.ElementTree as ET
from collections import OrderedDict

NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
RANK = {'PASS': 0, 'NOT TESTED': 1, 'FAIL': 2}

def seconds(d):
    try:
        h, m, s = d.split(':'); return int(h) * 3600 + int(m) * 60 + float(s)
    except Exception: return 0.0

def read_trx(folder):
    """test name -> (status, seconds, note); None when the job left no .trx at all."""
    files = sorted(glob.glob(os.path.join(folder, '**', '*.trx'), recursive=True))
    if not files: return None
    out = {}
    for f in files:
        try: root = ET.parse(f).getroot()
        except ET.ParseError as e: out['__broken__' + f] = ('NOT TESTED', 0, 'unreadable trx: %s' % e); continue
        for r in root.iter(NS + 'UnitTestResult'):
            name, outcome = r.get('testName'), r.get('outcome')
            text = ' '.join(x.text or '' for x in r.iter() if x.tag in (NS + 'StdOut', NS + 'Message', NS + 'StdErr'))
            msg = ' '.join((x.text or '').strip() for x in r.iter(NS + 'Message'))[:400]
            if outcome == 'Passed':
                st, note = ('NOT TESTED', 'the test said: ' + text[text.find('NOT TESTED'):][:200].strip()) if 'NOT TESTED' in text else ('PASS', '')
            elif outcome == 'Failed':
                st, note = ('NOT TESTED', msg) if 'NOT TESTED' in msg else ('FAIL', msg)
            else:
                st, note = 'NOT TESTED', 'outcome ' + str(outcome) + (': ' + msg if msg else '')
            prev = out.get(name)
            if prev is None or RANK[st] > RANK[prev[0]]: out[name] = (st, seconds(r.get('duration') or '0:0:0'), note)
    return out

def meta(folder):
    try: return json.load(open(os.path.join(folder, 'meta.json')))
    except Exception: return {}

def main():
    a = argparse.ArgumentParser()
    a.add_argument('--expected', required=True); a.add_argument('--results', required=True); a.add_argument('--out', required=True)
    a.add_argument('--control', action='store_true', help='a serial control run of all the tests is expected in control/')
    a.add_argument('--title', default='QA shards')
    o = a.parse_args()
    expected = json.load(open(o.expected))
    os.makedirs(o.out, exist_ok=True)
    per_test = OrderedDict()   # test -> list of {job, status, sec, note}
    jobs = []
    for jid in sorted(expected):
        folder = os.path.join(o.results, 'shard-' + jid)
        res, m = read_trx(folder), meta(folder)
        state = 'no results (cancelled, did not start, or crashed before writing)' if res is None else 'results'
        jobs.append({'job': jid, 'state': state, 'meta': m, 'tests': len(expected[jid])})
        for t in expected[jid]:
            st, sec, note = ('NOT TESTED', 0, 'job ' + jid + ': ' + state) if res is None else res.get(t, ('NOT TESTED', 0, 'job ' + jid + ': not in its results (not run)'))
            per_test.setdefault(t, []).append({'job': jid, 'status': st, 'sec': sec, 'note': note})
        if res:
            for t in res:
                if t not in expected[jid] and not t.startswith('__broken__'):
                    per_test.setdefault('UNEXPECTED ' + t, []).append({'job': jid, 'status': 'NOT TESTED', 'sec': 0, 'note': 'ran in ' + jid + ' but not planned there - check the shard filter'})
    verdict = {}
    for t, runs in per_test.items():
        worst = max(runs, key=lambda r: RANK[r['status']])['status']
        verdict[t] = worst
    control = None
    if o.control:
        cres = read_trx(os.path.join(o.results, 'control'))
        control = {t: ('NOT TESTED' if cres is None else cres.get(t, ('NOT TESTED', 0, ''))[0]) for t in per_test if not t.startswith('UNEXPECTED ')}
    # durations per class (the longest repetition) - the next plan balances by it
    dur = {}
    for t, runs in per_test.items():
        if t.startswith('UNEXPECTED '): continue
        c = t.split('(', 1)[0].rsplit('.', 1)[0]
        best = {}
        for r in runs: best[r['job'].split('-')[0]] = max(best.get(r['job'].split('-')[0], 0), r['sec'])
        dur[c] = round(dur.get(c, 0) + max(best.values() or [0]), 1)
    counts = {k: sum(1 for v in verdict.values() if v == k) for k in ('PASS', 'FAIL', 'NOT TESTED')}
    diffs = [t for t in (control or {}) if control[t] != verdict[t]]
    json.dump({'title': o.title, 'counts': counts, 'verdict': verdict, 'runs': per_test, 'jobs': jobs,
               'control': control, 'control_differences': diffs}, open(os.path.join(o.out, 'results.json'), 'w'), indent=1)
    json.dump(dur, open(os.path.join(o.out, 'durations.json'), 'w'), indent=1, sort_keys=True)
    L = ['# ' + o.title, '',
         '**%d tests: %d PASS, %d FAIL, %d NOT TESTED** (a test is PASS only when every job that had to run it reports PASS)' % (len(verdict), counts['PASS'], counts['FAIL'], counts['NOT TESTED']), '']
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
            notes = '; '.join(sorted(set(r['job'] + ': ' + r['note'][:160] for r in runs if r['status'] != 'PASS')))[:700]
            L.append('| %s | %s | %s - %s |' % (t, verdict[t], tally, notes.replace('|', '/').replace('\n', ' ')))
    if reps > 1:
        L += ['', '## Repetitions', '', '| test | PASS | FAIL | NOT TESTED |', '|---|---|---|---|']
        for t, runs in per_test.items():
            L.append('| %s | %d | %d | %d |' % (t, *[sum(1 for r in runs if r['status'] == k) for k in ('PASS', 'FAIL', 'NOT TESTED')]))
    if control is not None:
        L += ['', '## Serial control vs shards', '',
              ('**SAME** - every one of %d tests has the same result serially and sharded' % len(control)) if not diffs else
              '**DIFFERENT in %d tests** - the sharded run is NOT proven equal to the serial run:' % len(diffs)]
        for t in diffs: L.append('- %s: serial %s, sharded %s' % (t, control[t], verdict[t]))
    open(os.path.join(o.out, 'summary.md'), 'w').write('\n'.join(L) + '\n')
    print('\n'.join(L[:3]))
    ok = counts['FAIL'] == 0 and counts['NOT TESTED'] == 0 and not diffs and (control is None or all(v == 'PASS' for v in control.values()))
    sys.exit(0 if ok else 1)

if __name__ == '__main__': main()
