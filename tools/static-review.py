#!/usr/bin/env python3
"""
Static review by CLASSES of bugs (not single instances): every place in the product code that matches a class.
Each hit is then triaged by a person / the developer in tools/static-review-triage.json (id → verdict + reason), so a
new hit of a known class appears as UNTRIAGED in the next run.
  python3 tools/static-review.py            → docs/STATIC-REVIEW.md, exit 1 when a hit is untriaged or marked BUG
"""
import json, os, re, sys, hashlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = [os.path.join(ROOT, 'src', d) for d in ('Core', 'Server', 'Agent', 'Setup', 'ClientApp')]

CLASSES = [
    ('swallowed-exception', 'A catch that does nothing: the failure disappears (can turn a failure into a success)',
     r'catch\s*(\([^)]*\))?\s*\{\s*\}'),
    ('catch-all-continue', 'catch (Exception) that only logs and goes on: is the operation then reported correctly?',
     r'catch\s*\(\s*Exception\s+\w+\s*\)\s*\{[^{}]*(SysLog\.Write|say\(|warn\(|Warn\(|Info\()[^{}]*\}'),
    ('raw-process', 'An external program started outside ProcessRunner (no limit / streams / tree kill)',
     r'Process\.Start\('),
    ('wait-no-limit', 'WaitForExit() without a time limit', r'WaitForExit\(\s*\)'),
    ('readtoend-process', 'Reading a program\'s output to the end before waiting (old deadlock pattern)', r'Standard(Output|Error)\.ReadToEnd\(\)'),
    ('default-success', 'A missing result taken as success', r'\?\?\s*"BS_STOP_SUCCESS"'),
    ('non-atomic-state', 'State written with File.WriteAllText/WriteAllBytes (a crash in the middle leaves half a file)',
     r'File\.Write(AllText|AllBytes|AllLines)\('),
    ('sync-over-async', '.Result / .Wait() on a task (thread starvation, deadlock)', r'\.(Result|Wait\(\))\s*[;)]'),
    ('sql-concat', 'SQL built by concatenation', r'(SELECT|INSERT|UPDATE|DELETE)[^"]*"\s*\+'),
    ('path-from-request', 'A path built from a request value (path traversal)', r'Path\.Combine\([^;]*(Q\(ctx|b\[")'),
    ('static-dictionary', 'A static Dictionary (shared between requests / threads): every use must be under a lock',
     r'static\s+(readonly\s+)?Dictionary<'),
    ('thread-sleep-loop', 'A wait loop with Thread.Sleep without an end condition in time', r'while\s*\([^)]*\)\s*Thread\.Sleep'),
    ('web-no-timeout', 'An HTTP request without a time limit', r'WebRequest\.Create\('),
    ('timer-no-dispose', 'A Timer field (must be disposed when its owner stops)', r'new\s+(System\.Threading\.)?Timer\('),
    ('async-void', 'async void (an exception kills the process)', r'async\s+void\s'),
]

def scan():
    hits = []
    for base in SRC:
        for root, _, files in os.walk(base):
            if '/bin' in root or '/obj' in root: continue
            for f in files:
                if not f.endswith('.cs'): continue
                p = os.path.join(root, f); rel = os.path.relpath(p, ROOT); text = open(p, encoding='utf-8').read()
                lines = text.split('\n')
                for cid, what, rx in CLASSES:
                    for m in re.finditer(rx, text, re.S if cid == 'catch-all-continue' else 0):
                        ln = text.count('\n', 0, m.start()) + 1
                        code = lines[ln - 1].strip()
                        key = hashlib.sha1((cid + rel + re.sub(r'\s+', ' ', code)).encode()).hexdigest()[:10]
                        hits.append(dict(id=key, cls=cid, file=rel, line=ln, code=code[:220]))
    return hits

if __name__ == '__main__':
    hits = scan()
    tri_path = os.path.join(ROOT, 'tools', 'static-review-triage.json')
    tri = json.load(open(tri_path)) if os.path.exists(tri_path) else {}
    by = {}
    for h in hits: by.setdefault(h['cls'], []).append(h)
    out = ['# סקירה סטטית לפי מחלקות באגים — נוצרת ע"י `tools/static-review.py`', '',
           'כל מופע מסווג ב-`tools/static-review-triage.json`: **OK** (נבדק ותקין — עם הסיבה), **FIXED** (היה באג ותוקן — עם הבדיקה), **BUG** (פתוח).', '',
           '| מחלקה | תיאור | מופעים | OK | FIXED | BUG | לא סווג |', '|---|---|---|---|---|---|---|']
    tot = dict(OK=0, FIXED=0, BUG=0, UNTRIAGED=0); ids = set(h['id'] for h in hits)
    for cid, what, _ in CLASSES:
        hs = by.get(cid, []); c = dict(OK=0, FIXED=0, BUG=0, UNTRIAGED=0)
        for h in hs: c[tri.get(h['id'], {}).get('verdict', 'UNTRIAGED')] += 1
        c['FIXED'] += sum(1 for k, v in tri.items() if v.get('verdict') == 'FIXED' and v.get('cls') == cid and k not in ids)
        for k in c: tot[k] += c[k]
        out.append('| %s | %s | %d | %d | %d | %d | %d |' % (cid, what, len(hs), c['OK'], c['FIXED'], c['BUG'], c['UNTRIAGED']))
    out.append('| **סה"כ** | | **%d** | %d | %d | %d | %d |' % (len(hits), tot['OK'], tot['FIXED'], tot['BUG'], tot['UNTRIAGED']))
    out += ['', '## תוקנו (כבר לא בקוד)', '', '| מחלקה | מקום | באג ובדיקה |', '|---|---|---|']
    for k, v in sorted(tri.items()):
        if v.get('verdict') == 'FIXED' and k not in ids: out.append('| %s | %s | %s |' % (v.get('cls', ''), v.get('where', ''), v.get('why', '')))
    out += ['', '## כל המופעים', '', '| מזהה | מחלקה | מקום | קוד | סיווג | נימוק |', '|---|---|---|---|---|---|']
    for h in sorted(hits, key=lambda h: (h['cls'], h['file'], h['line'])):
        t = tri.get(h['id'], {})
        out.append('| %s | %s | %s:%d | `%s` | %s | %s |' % (h['id'], h['cls'], h['file'], h['line'], h['code'].replace('|', '\\|').replace('`', "'"), t.get('verdict', '**לא סווג**'), t.get('why', '').replace('|', '/')))
    os.makedirs(os.path.join(ROOT, 'docs'), exist_ok=True)
    open(os.path.join(ROOT, 'docs', 'STATIC-REVIEW.md'), 'w').write('\n'.join(out) + '\n')
    json.dump(hits, open(os.path.join(ROOT, 'tools', 'static-review-hits.json'), 'w'), indent=1)
    print(json.dumps(tot), len(hits), 'hits')
    sys.exit(1 if tot['BUG'] or tot['UNTRIAGED'] else 0)
