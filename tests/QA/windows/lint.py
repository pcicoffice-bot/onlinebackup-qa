#!/usr/bin/env python3
"""
Lint of the Windows QA scripts: a check returns @(ok, what was seen), and in PowerShell the comma binds tighter than
-and / -or / -eq: @(x -and y, "text") is x -and (y, "text") - one True, a PASS that ignored y (found in W1 run 1,
before any result counted). Every first element with an operator must be one parenthesised group.
The same holds for the SECOND element: @(ok, (list) -join '; ') is (ok, (list)) -join '; ' - ONE string "False; ...",
whose first character made [bool] True: a PASS whatever ok was (W03 installed programs, W04 and W19 "connected" -
found by QA agent G before a run counted). A second element with an operator must be one parenthesised group too.
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
BINOP = re.compile(r'(^|\s)(-(join|f|replace|creplace|split|and|or|eq|ne|like|notlike|match|notmatch|gt|lt|ge|le|contains|notcontains|in|notin|is|isnot|as|band|bor|xor)\b|[+*/%]\s)', re.I)
def top_level_text(body):
    """the text outside strings and bracket groups (where an operator binds at this level)"""
    out = []; i = 0
    while i < len(body):
        c = body[i]
        if c in '\'"': j = scan_string(body, i); out.append(' ' * (j - i)); i = j; continue
        if c in '([{': j = match(body, i); out.append(' ' * (j - i)); i = j; continue
        out.append(c); i += 1
    return ''.join(out)
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
            a, b = body[:top_commas(body)[0]], body[top_commas(body)[0] + 1:]
            b2 = b.strip()
            if b2 and BINOP.search(top_level_text(b2)):
                body = a + ', (' + b2 + ')'; n += 1
        res.append(s[i:j] + '@(' + fix(body) + ')')
        i = end
    return ''.join(res)
# A function that returns server items (hashtables) gives ONE hashtable when there is one item: (Runs ...).Count is
# then its number of fields (15), and $r[-1] its key -1 (nothing). Every use must be wrapped: @(Runs ...).
LISTFN = re.compile(r'(?<!@)\((Runs|LiveRuns|Items|SetsOf|Connected|NewRuns)\b|=\s*(Runs|LiveRuns|SetsOf|NewRuns)\s')
def unwrapped(s):
    return [(n, l) for n, l in enumerate(s.splitlines(), 1) if LISTFN.search(l) and not l.lstrip().startswith(('#', 'function'))]
if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if a != '--fix']; bad = 0
    for p in args:
        s = open(p, encoding='utf-8').read(); t = fix(s)
        for n, l in unwrapped(s):
            bad += 1; print('%s:%d: a list of server items used without @( ): one item is a hashtable whose .Count is its field count:\n  %s' % (p, n, l.strip()[:200]))
        # Q9 (W1 run 5): a hashtable with a key named keys / values / count - $h.keys is the hashtable's own Keys
        # collection, never the value: RunMark's "keys" made NewRuns throw (the self-test stopped the run)
        for n, l in enumerate(s.splitlines(), 1):
            if re.search(r'@\{[^}]*(?:^|[;{\s])(?:keys|values|count)\s*=', l, re.I):
                bad += 1; print('%s:%d: a hashtable key named keys/values/count is hidden by the hashtable\'s own property:\n  %s' % (p, n, l.strip()[:200]))
        # Windows PowerShell 5.1 reads a file without a BOM as the ANSI code page: one non-ASCII character (an em dash in a
        # comment) can end a string early and break the whole script on Windows only - every script stays ASCII
        for n, l in enumerate(s.splitlines(), 1):
            if any(ord(c) > 127 for c in l):
                bad += 1; print('%s:%d: a non-ASCII character (PowerShell 5.1 reads this file as ANSI):\n  %s' % (p, n, l.strip()[:200]))
        if t != s:
            bad += 1
            if '--fix' in sys.argv: open(p, 'w', encoding='utf-8').write(t); print(p, 'fixed')
            else:
                for n, (a, b) in enumerate(zip(s.splitlines(), t.splitlines()), 1):
                    if a != b: print('%s:%d: a check whose first or second element is not one parenthesised group:\n  %s' % (p, n, a.strip()[:200]))
    sys.exit(1 if bad and ('--fix' not in sys.argv or any(any(ord(c) > 127 for c in open(p, encoding='utf-8').read()) for p in args) or any(unwrapped(open(p, encoding='utf-8').read()) for p in args)) else 0)
