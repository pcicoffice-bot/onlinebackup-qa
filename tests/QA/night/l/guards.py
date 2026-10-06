#!/usr/bin/env python3
"""Lists every xUnit test whose body returns early (a silent no-op that xUnit reports as Passed)."""
import os, re, sys
tdir = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Tests')
for f in sorted(os.listdir(tdir)):
    if not f.endswith('.cs'): continue
    src = open(os.path.join(tdir, f), encoding='utf-8').read()
    for m in re.finditer(r'\[(Fact|Theory)[^\]]*\](?:\s*(?:\[[^\]]*\]|//[^\n]*))*\s*public\s+(?:async\s+\w+\s+|void\s+)(\w+)\s*\(', src):
        start = src.index('{', m.end()); depth = 0
        for e in range(start, len(src)):
            if src[e] == '{': depth += 1
            elif src[e] == '}':
                depth -= 1
                if depth == 0: break
        body = src[start:e]
        # only top-level guards (depth 1) before the first using/var
        head = body[:600]
        g = re.search(r'^\s*if \((.*)\)\s*return;', head, re.M)
        if g and body.index(g.group(0).strip()) < 400:
            line = src[:start].count('\n') + head[:g.start()].count('\n') + 1
            print('%s:%d\t%s\t%s' % (f, line, m.group(2), g.group(1)))
