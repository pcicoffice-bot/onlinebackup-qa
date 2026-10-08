#!/usr/bin/env python3
# Night P mutation proof. usage: mut.py ID FILE OLD NEW FILTER
# Applies ONE temporary text change to a product file in this worktree, builds, runs only FILTER, logs, and ALWAYS
# reverts (git checkout) and rebuilds. Never committed: the log shows the diff and the reverted `git diff --stat src`.
import sys, subprocess, os, re, time
S = '/tmp/claude-0/-home-user-golan-crm/a604ba29-09da-5998-b6c7-5aefd7765d0d/scratchpad'
W = S + '/agent-p'; LOG = W + '/tests/QA/night/p/mutation-logs'
mid, f, old, new, flt = sys.argv[1:6]
env = dict(os.environ); env['OB_RESTIC'] = S + '/restic'
assert not subprocess.run(['git', '-C', W, 'diff', '--stat', 'src'], capture_output=True, text=True).stdout.strip(), 'src is not clean'
p = os.path.join(W, f); s = open(p, encoding='utf-8').read(); n = s.count(old)
if n != 1: print('MUTATION NOT APPLIED: old text found %d times' % n); sys.exit(2)
open(p, 'w', encoding='utf-8').write(s.replace(old, new))
try:
    d = subprocess.run(['git', '-C', W, 'diff', '--', f], capture_output=True, text=True).stdout
    t = time.time()
    r = subprocess.run(['dotnet', 'test', 'tests/Tests', '-nologo', '-v', 'q', '--logger', 'console;verbosity=normal', '--filter', flt], cwd=W, env=env, capture_output=True, text=True, timeout=5400)
    o = r.stdout + r.stderr
    fails = sorted(set(re.findall(r'^\s+Failed (OnlineBackup\.Tests\.\S+)', o, re.M)))
    passed = sorted(set(re.findall(r'^\s+Passed (OnlineBackup\.Tests\.\S+)', o, re.M)))
    open(os.path.join(LOG, mid + '.log'), 'w').write('MUTATION ' + mid + '\nFILTER ' + flt + '\n' + d + '\n' + o)
    print('%s (%ds): failed %s passed %s' % (mid, int(time.time() - t), fails, passed))
    print('VERDICT:', 'BUILD ERROR' if 'error CS' in o else ('KILLED' if fails else 'SURVIVED'))
finally:
    subprocess.run(['git', '-C', W, 'checkout', '--', f])
    subprocess.run(['dotnet', 'build', 'tests/Tests', '-nologo', '-v', 'q'], cwd=W, capture_output=True)
    print('reverted; git diff --stat src:', subprocess.run(['git', '-C', W, 'diff', '--stat', 'src'], capture_output=True, text=True).stdout.strip() or 'empty')
