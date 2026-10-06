#!/usr/bin/env python3
"""
Lint of the Windows QA scripts: a check returns @(ok, what was seen), and in PowerShell the comma binds tighter than
-and / -or / -eq: @(x -and y, "text") is x -and (y, "text") - one True, a PASS that ignored y (found in W1 run 1,
before any result counted). Every first element with an operator must be one parenthesised group.
  python3 lint.py [--fix] file.ps1 ...     (exit 1 when a check is written the unsafe way)
"""
import sys, re
def scan_string(s, i):
    q = s[i]; i += 1
    while i < len(s):
        c = s[i]
        if q == "'" :
            if c == "'" and i + 1 < len(s) and s[i+1] == "'": i += 2; continue
            if c == "'": return i + 1
            i += 1; continue
        if c == '`': i += 2; continue
        if c == '"': return i + 1
        if c == '$' and i + 1 < len(s) and s[i+1] == '(':
            i = match(s, i + 1) ; continue
        i += 1
    return i
def match(s, i):  # s[i] is an opening ( [ {; returns index after its closing
    op = s[i]; cl = {'(': ')', '[': ']', '{': '}'}[op]; depth = 0
    while i < len(s):
        c = s[i]
        if c in '\'"': i = scan_string(s, i); continue
        if c == '#' and op != '(' : pass
        if c in '([{': depth += 1
        elif c in ')]}':
            depth -= 1
            if depth == 0: return i + 1
        i += 1
    return i
def top_commas(body):
    out = []; i = 0; depth = 0
    while i < len(body):
        c = body[i]
        if c in '\'"': i = scan_string(body, i); continue
        if c in '([{': i = match(body, i); continue
        if c == ',': out.append(i)
        i += 1
    return out
def fix(s):
    res = []; i = 0; n = 0
    while True:
        j = s.find('@(', i)
        if j < 0: res.append(s[i:]); break
        end = match(s, j + 1)
        body = s[j+2:end-1]
        cs = top_commas(body)
        if len(cs) == 1:
            a, b = body[:cs[0]], body[cs[0]:]
            a2 = a.strip()
            whole = a2.startswith('(') and match(a2, 0) == len(a2)
            simple = re.fullmatch(r'\$?[\w.:\[\]\']+', a2) is not None
            if not whole and not simple and re.search(r'-(and|or|eq|ne|like|notlike|match|notmatch|gt|lt|ge|le|not)\b|\s', a2):
                body = ' ' * (len(a) - len(a.lstrip())) + '(' + a2 + ')' + b; n += 1
        res.append(s[i:j] + '@(' + fix(body) + ')')
        i = end
    return ''.join(res)
if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--fix']; bad = 0
    for p in args:
        s = open(p, encoding='utf-8').read(); t = fix(s)
        if t != s:
            bad += 1
            if '--fix' in sys.argv: open(p, 'w', encoding='utf-8').write(t); print(p, 'fixed')
            else:
                for n, (a, b) in enumerate(zip(s.splitlines(), t.splitlines()), 1):
                    if a != b: print('%s:%d: a check whose first element is not one parenthesised group:\n  %s' % (p, n, a.strip()[:200]))
    sys.exit(1 if bad and '--fix' not in sys.argv else 0)
