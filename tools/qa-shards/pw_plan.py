#!/usr/bin/env python3
"""QA shards for the Playwright journeys: the plan. Input: `npx playwright test --list --reporter=json` of THIS build.
Every journey starts its own server and agent (playwright.config.ts), so the unit of a shard is a spec FILE: its tests run
one after another exactly as in the serial run (workers: 1). Files are balanced over N shards by their number of tests.
Writes matrix.json, expected.json ({job: ["file > title", ...]}), control-files.txt (every file: the serial control run).
  pw_plan.py --list list.json --shards N --out DIR"""
import argparse, json, os, sys

def names(list_json):
    """{file: [test names]} - a name is "file > describe > title" (the same in the run's JSON report)."""
    out = {}
    def walk(suite, path, file):
        f = suite.get('file') or file
        title = suite.get('title') or ''
        p = path + ([title] if title and title != f and not title.endswith('.ts') else [])
        for spec in suite.get('specs', []):
            out.setdefault(f, []).append(' > '.join([f] + p + [spec['title']]))
        for s in suite.get('suites', []): walk(s, p, f)
    for s in json.load(open(list_json)).get('suites', []): walk(s, [], s.get('file') or s.get('title'))
    return out

def main():
    a = argparse.ArgumentParser(); a.add_argument('--list', required=True); a.add_argument('--shards', type=int, default=4); a.add_argument('--out', required=True)
    o = a.parse_args()
    files = names(o.list)
    if not files: sys.exit('pw_plan: no tests listed - nothing to run is NOT a pass')
    n = max(1, min(o.shards, len(files)))
    bins = [{'files': [], 'tests': 0} for _ in range(n)]
    for f in sorted(files, key=lambda f: (-len(files[f]), f)):
        b = min(bins, key=lambda b: (b['tests'], len(b['files']))); b['files'].append(f); b['tests'] += len(files[f])
    jobs, expected = [], {}
    for i, b in enumerate(bins, 1):
        if not b['files']: continue
        jid = 's%02d-r01' % i
        jobs.append({'id': jid, 'files': ' '.join(sorted(b['files']))})
        expected[jid] = sorted(t for f in b['files'] for t in files[f])
    os.makedirs(o.out, exist_ok=True)
    json.dump({'include': jobs}, open(os.path.join(o.out, 'matrix.json'), 'w'))
    json.dump(expected, open(os.path.join(o.out, 'expected.json'), 'w'), indent=1)
    open(os.path.join(o.out, 'control-files.txt'), 'w').write(' '.join(sorted(files)))
    json.dump([], open(os.path.join(o.out, 'disappeared.json'), 'w'))
    for j in jobs: print('%s  %3d tests  %s' % (j['id'], len(expected[j['id']]), j['files']))

if __name__ == '__main__': main()
