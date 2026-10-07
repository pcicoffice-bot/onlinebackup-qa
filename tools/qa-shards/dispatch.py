#!/usr/bin/env python3
"""QA dispatcher: the dynamic queue with a reservation for certification, and the runners' utilization log.

GitHub (this account's plan) runs at most CAP jobs at once across ALL its repositories - the private gate, the public
mirror's certification runs (qa.yml: C1, Windows robot) and the QA shards share them. GitHub has no priorities, so the
orchestrator keeps the room itself: discovery (qa-shards-* branches) may use only what is left after RESERVE runners
stay free for certification, and never more than certification leaves queued.

  dispatch.py status                     busy / queued jobs, per repository and kind (certification / discovery)
  dispatch.py budget [--reserve 8]       how many discovery jobs may start now (0 = wait)
  dispatch.py sample --out FILE [--every 60] [--minutes N]
                                         every N seconds one line: time, running and queued jobs per kind - the utilization
  dispatch.py report FILE                utilization summary of a sample log
  dispatch.py reps --fail-rate P [--confidence 0.99]
                                         Flaky Hunter: clean repetitions needed before an intermittent failure counts as
                                         gone - with the failure rate P measured BEFORE the fix, (1-P)^n <= 1-confidence

Certification = every run of the private repository + the public mirror's qa.yml runs (qa-run* branches) and the gate.
Discovery = the public mirror's qa-shards-* and qa-pw-* (SAME proof) runs. A discovery run that waited in the queue until GitHub dropped it is
NOT TESTED in its aggregate (the aggregator already says so); this tool never cancels anything.
"""
import argparse, json, subprocess, sys, time
from datetime import datetime, timezone

OWNER = 'pcicoffice-bot'
REPOS = ['golan-crm', 'onlinebackup-qa']
CAP = 20

def api(path):
    out = subprocess.run(['gh', 'api', path], capture_output=True, text=True)
    if out.returncode != 0: raise RuntimeError(out.stderr.strip()[:300])
    return json.loads(out.stdout)

def kind(repo, run):
    # qa-pw-* is the journeys' SAME proof (pw-shards.yml), not certification evidence - it gets no reserved room
    return 'discovery' if repo == 'onlinebackup-qa' and (run.get('head_branch') or '').startswith(('qa-shards-', 'qa-pw-')) else 'certification'

def jobs_now():
    """[(repo, kind, run id, job name, status)] for every job of every queued or running run."""
    rows = []
    for repo in REPOS:
        seen = set()
        for st in ('in_progress', 'queued'):
            for run in api('repos/%s/%s/actions/runs?status=%s&per_page=50' % (OWNER, repo, st)).get('workflow_runs', []):
                if run['id'] in seen: continue
                seen.add(run['id'])
                page = 1
                while True:
                    js = api('repos/%s/%s/actions/runs/%d/jobs?per_page=100&page=%d' % (OWNER, repo, run['id'], page)).get('jobs', [])
                    for j in js:
                        if j['status'] in ('in_progress', 'queued', 'waiting', 'pending'):
                            rows.append((repo, kind(repo, run), run['id'], j['name'], 'running' if j['status'] == 'in_progress' else 'queued'))
                    if len(js) < 100: break
                    page += 1
    return rows

def counts(rows):
    c = {(k, s): 0 for k in ('certification', 'discovery') for s in ('running', 'queued')}
    for r in rows: c[(r[1], r[4])] += 1
    return c

def budget(rows, reserve):
    c = counts(rows)
    running = c[('certification', 'running')] + c[('discovery', 'running')]
    # certification that is waiting gets the room first; RESERVE runners always stay free for certification
    return max(0, CAP - running - reserve - c[('certification', 'queued')]), c

def main():
    a = argparse.ArgumentParser(); sub = a.add_subparsers(dest='cmd', required=True)
    sub.add_parser('status')
    b = sub.add_parser('budget'); b.add_argument('--reserve', type=int, default=8)
    s = sub.add_parser('sample'); s.add_argument('--out', required=True); s.add_argument('--every', type=int, default=60); s.add_argument('--minutes', type=int, default=0)
    r = sub.add_parser('report'); r.add_argument('file')
    q = sub.add_parser('reps'); q.add_argument('--fail-rate', type=float, required=True); q.add_argument('--confidence', type=float, default=0.99)
    o = a.parse_args()
    if o.cmd == 'status':
        rows = jobs_now(); c = counts(rows)
        for k in ('certification', 'discovery'): print('%-13s running %2d  queued %2d' % (k, c[(k, 'running')], c[(k, 'queued')]))
        for x in rows: print('  %s %-11s %s %-40s %s' % x)
    elif o.cmd == 'budget':
        n, c = budget(jobs_now(), o.reserve)
        print(n)
    elif o.cmd == 'sample':
        end = time.time() + o.minutes * 60 if o.minutes else None
        while end is None or time.time() < end:
            try:
                c = counts(jobs_now())
                line = {'t': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), **{'%s_%s' % k: v for k, v in c.items()}}
            except Exception as e:
                line = {'t': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), 'error': str(e)[:200]}
            with open(o.out, 'a') as f: f.write(json.dumps(line) + '\n')
            time.sleep(o.every)
    elif o.cmd == 'reps':
        import math
        if not 0 < o.fail_rate < 1: sys.exit('fail rate between 0 and 1 (measured before the fix)')
        n = math.ceil(math.log(1 - o.confidence) / math.log(1 - o.fail_rate))
        print('%d clean repetitions (all PASS, parallel AND serial on one machine) for %.0f %% confidence at a failure rate of %.2f' % (n, 100 * o.confidence, o.fail_rate))
    elif o.cmd == 'report':
        L = [json.loads(x) for x in open(o.file) if x.strip()]
        L = [x for x in L if 'error' not in x]
        if not L: print('no samples'); return
        busy = [x['certification_running'] + x['discovery_running'] for x in L]
        print('samples %d, %s .. %s' % (len(L), L[0]['t'], L[-1]['t']))
        print('runners busy: mean %.1f of %d (%.0f %%), max %d; idle samples (0 busy) %d' % (sum(busy) / len(L), CAP, 100.0 * sum(busy) / len(L) / CAP, max(busy), sum(1 for b in busy if b == 0)))
        for k in ('certification', 'discovery'):
            print('  %-13s running mean %.1f, queued mean %.1f, queued max %d' % (k, sum(x[k + '_running'] for x in L) / len(L), sum(x[k + '_queued'] for x in L) / len(L), max(x[k + '_queued'] for x in L)))

if __name__ == '__main__': main()
