#!/usr/bin/env python3
"""QA shards - the plan: which tests each isolated runner runs.

  plan.py --tests list.txt --shards N --repeat R [--select "PATTERN ..."] [--durations durations.json] --out DIR

list.txt is the output of `dotnet test --list-tests` (the names under "The following Tests are available:"); that
command ignores --filter, so the selection is made here: a test is selected when its full name contains one of the
space-separated patterns (no patterns: every test).
Tests are split by CLASS (a class's tests share fixtures and run one after another as in the serial run - the shard runs
them exactly as the serial run would); classes are balanced over N shards by their known duration (durations.json from an
earlier aggregate) or, without it, by test count. --repeat R runs every shard R times on separate runners (an
intermittent failure: R independent repetitions at once instead of one after another).

Writes DIR/matrix.json (the GitHub matrix) and DIR/expected.json (every job and the exact tests it must report - the
aggregator marks anything missing NOT TESTED).
"""
import argparse, json, os, re, sys

def read_tests(path):
    names, on = [], False
    for line in open(path, encoding='utf-8', errors='replace'):
        line = line.rstrip('\r\n')
        if 'The following Tests are available' in line: on = True; continue
        if on and line.startswith('    ') and line.strip(): names.append(line.strip())
    return names

def class_of(test):
    # "Ns.Class.Method" or "Ns.Class.Method(arg: 1)" -> "Ns.Class"
    return test.split('(', 1)[0].rsplit('.', 1)[0]

def method_of(test):
    return test.split('(', 1)[0]

def plan(all_tests, shards, repeat, patterns, durations):
    picked = set(method_of(t) for t in all_tests if not patterns or any(p in t for p in patterns))
    tests = [t for t in all_tests if method_of(t) in picked]   # a theory runs all its cases: all of them are expected
    if not tests: raise SystemExit('plan: no tests selected - nothing to run is NOT a pass')
    whole = {}
    for t in all_tests: whole.setdefault(class_of(t), []).append(t)
    classes = {}
    for t in tests: classes.setdefault(class_of(t), []).append(t)
    shards = max(1, min(shards, len(classes)))
    weight = lambda c: durations.get(c, 0) or len(classes[c]) * 10.0
    bins = [{'classes': [], 'weight': 0.0} for _ in range(shards)]
    for c in sorted(classes, key=lambda c: (-weight(c), c)):   # longest first into the lightest shard
        b = min(bins, key=lambda b: (b['weight'], len(b['classes'])))
        b['classes'].append(c); b['weight'] += weight(c)
    jobs, expected = [], {}
    for i, b in enumerate(bins, 1):
        if not b['classes']: continue
        parts = []
        for c in sorted(b['classes']):
            if len(classes[c]) == len(whole[c]):
                parts.append('FullyQualifiedName~' + c + '.')   # the trailing dot: "Ns.A." never selects "Ns.AB"
            else:   # only some tests of the class: each by its exact name (a theory's cases share it)
                parts += ['FullyQualifiedName=' + m for m in sorted(set(method_of(t) for t in classes[c]))]
        f = '|'.join(parts)
        for r in range(1, repeat + 1):
            jid = 's%02d-r%02d' % (i, r)
            jobs.append({'id': jid, 'shard': i, 'rep': r, 'filter': f, 'est_min': round(b['weight'] / 60, 1)})
            expected[jid] = sorted(t for c in b['classes'] for t in classes[c])
    return jobs, expected, tests

def main():
    a = argparse.ArgumentParser()
    a.add_argument('--tests', required=True); a.add_argument('--shards', type=int, default=4)
    a.add_argument('--repeat', type=int, default=1); a.add_argument('--select', default='')
    a.add_argument('--durations', default=''); a.add_argument('--out', required=True)
    a.add_argument('--max-jobs', type=int, default=256)
    o = a.parse_args()
    durations = json.load(open(o.durations)) if o.durations and os.path.exists(o.durations) else {}
    jobs, expected, tests = plan(read_tests(o.tests), o.shards, max(1, o.repeat), o.select.split(), durations)
    if len(jobs) > o.max_jobs: raise SystemExit('plan: %d jobs is more than GitHub allows in one matrix (%d)' % (len(jobs), o.max_jobs))
    os.makedirs(o.out, exist_ok=True)
    json.dump({'include': jobs}, open(os.path.join(o.out, 'matrix.json'), 'w'))
    json.dump(expected, open(os.path.join(o.out, 'expected.json'), 'w'), indent=1)
    # the serial control run: the same tests in ONE job, one after another (the proof that sharding changes nothing)
    open(os.path.join(o.out, 'control-filter.txt'), 'w').write('|'.join(sorted(set(j['filter'] for j in jobs if j['rep'] == 1))))
    for j in jobs: print('%s  ~%5.1f min  %3d tests' % (j['id'], j['est_min'], len(expected[j['id']])))

if __name__ == '__main__': main()
