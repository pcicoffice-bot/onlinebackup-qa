#!/usr/bin/env python3
"""Playwright's JSON report -> a .trx the QA aggregator reads (one UnitTestResult per test, same names as pw_plan.py).
passed -> Passed; failed / timedOut / interrupted -> Failed (with the error); skipped -> NotExecuted (NOT TESTED, never PASS).
A test with several results (there are no retries: retries: 0) takes its worst.
  pw2trx.py report.json out.trx"""
import json, sys
from xml.sax.saxutils import escape
RANK = {'passed': 0, 'skipped': 1, 'failed': 2, 'timedOut': 2, 'interrupted': 2}
def walk(suite, path, file, out):
    f = suite.get('file') or file
    title = suite.get('title') or ''
    p = path + ([title] if title and title != f and not title.endswith('.ts') else [])
    for spec in suite.get('specs', []):
        name = ' > '.join([f] + p + [spec['title']])
        worst, msg, dur = None, '', 0
        for t in spec.get('tests', []):
            for r in t.get('results', []) or [{'status': t.get('status') or 'skipped'}]:
                st = r.get('status', 'skipped'); dur += r.get('duration', 0) or 0
                if worst is None or RANK.get(st, 2) > RANK.get(worst, 2):
                    worst = st; msg = ((r.get('error') or {}).get('message') or '')[:2000]
                if st == 'skipped': msg = msg or ' '.join(a.get('description', '') for a in t.get('annotations', []))
        out.append((name, worst or 'skipped', msg, dur))
    for s in suite.get('suites', []): walk(s, p, f, out)
rep = json.load(open(sys.argv[1])); out = []
for s in rep.get('suites', []): walk(s, [], s.get('file') or s.get('title'), out)
rows = []
for name, st, msg, dur in out:
    outcome = {'passed': 'Passed', 'skipped': 'NotExecuted'}.get(st, 'Failed')
    secs = dur / 1000.0; h, rem = divmod(secs, 3600); m, s = divmod(rem, 60)
    rows.append('<UnitTestResult testName="%s" outcome="%s" duration="%02d:%02d:%06.3f"><Output>%s</Output></UnitTestResult>' % (
        escape(name, {'"': '&quot;'}), outcome, h, m, s, ('<ErrorInfo><Message>%s</Message></ErrorInfo>' % escape(msg)) if msg else ''))
open(sys.argv[2], 'w').write('<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>%s</Results></TestRun>' % ''.join(rows))
print('%d tests: %s' % (len(out), {k: sum(1 for x in out if x[1] == k) for k in sorted(set(x[1] for x in out))}))
