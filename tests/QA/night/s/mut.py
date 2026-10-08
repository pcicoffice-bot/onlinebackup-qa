#!/usr/bin/env python3
# Night round S mutation runner (adapted from tests/QA/night/l/mut.py by agent L).
# usage: mut.py ID FILE OLD NEW FILTER [k=v env ...]
#   applies ONE mutation (OLD->NEW, must be unique) in agent-s worktree, runs `dotnet test --filter FILTER`,
#   compares new failures against a cached baseline for that FILTER, then ALWAYS reverts the file.
#   verdict: KILLED (a test that passed clean now fails) or SURVIVED.
import sys, subprocess, os, re, time, json, hashlib
S = '/tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad'
W = S + '/agent-s'; LOG = S + '/agent-s/tests/QA/night/s/logs'; os.makedirs(LOG, exist_ok=True)
mid, f, old, new, flt = sys.argv[1:6]
env = dict(os.environ); env['OB_RESTIC'] = S + '/restic'
for kv in sys.argv[6:]: k, v = kv.split('=', 1); env[k] = v
def run(tag):
    t = time.time()
    r = subprocess.run(['dotnet', 'test', 'tests/Tests', '-nologo', '-v', 'q', '--logger', 'console;verbosity=normal', '--filter', flt], cwd=W, env=env, capture_output=True, text=True, timeout=5400)
    o = r.stdout + r.stderr
    fails = sorted(set(re.findall(r'^\s+Failed (OnlineBackup\.Tests\.\S+)', o, re.M)))
    tot = re.findall(r'Total tests: (\d+)', o); passed = re.findall(r'Passed: (\d+)', o)
    if 'error CS' in o: fails = ['BUILD ERROR']
    return dict(exit=r.returncode, fails=fails, total=tot[-1] if tot else '?', passed=passed[-1] if passed else '?', secs=int(time.time() - t), out=o)
key = hashlib.sha1((flt + json.dumps(sys.argv[6:])).encode()).hexdigest()[:12]
bf = os.path.join(LOG, 'baseline-' + key + '.json')
assert not subprocess.run(['git', '-C', W, 'diff', '--stat', 'src'], capture_output=True, text=True).stdout.strip(), 'src is not clean'
if os.path.exists(bf): base = json.load(open(bf))
else:
    base = run('baseline'); open(bf.replace('.json', '.log'), 'w').write(flt + '\n' + base['out']); base.pop('out'); json.dump(base, open(bf, 'w'))
print('baseline (%s): total %s passed %s fails %s' % (key, base['total'], base['passed'], base['fails']))
p = os.path.join(W, f); s = open(p, encoding='utf-8').read(); n = s.count(old)
if n != 1: print('MUTATION NOT APPLIED: old text found %d times' % n); sys.exit(2)
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
try:
    d = subprocess.run(['git', '-C', W, 'diff', '--', f], capture_output=True, text=True).stdout
    m = run('mutant')
    open(os.path.join(LOG, mid + '.log'), 'w').write('MUTATION ' + mid + '\nFILTER ' + flt + '\n' + d + '\n' + m['out'])
    newf = [x for x in m['fails'] if x not in base['fails']]
    print('%s mutant: total %s passed %s fails %s (%ds)' % (mid, m['total'], m['passed'], m['fails'], m['secs']))
    print('VERDICT:', 'BUILD ERROR' if 'BUILD ERROR' in m['fails'] else ('KILLED by ' + ', '.join(newf) if newf else 'SURVIVED'))
finally:
    subprocess.run(['git', '-C', W, 'checkout', '--', f])
    print('reverted; git diff --stat src:', subprocess.run(['git', '-C', W, 'diff', '--stat', 'src'], capture_output=True, text=True).stdout.strip() or 'empty')
