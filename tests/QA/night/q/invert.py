#!/usr/bin/env python3
"""Round Q: makes a temporary copy of a spec with ONE assertion inverted (e2e/zz-inv-<name>.spec.ts) — run it, expect a
failure at that line, then delete the copy (the original spec is never edited). Usage: invert.py <spec> <old> <new> <name>"""
import sys, os
spec, old, new, name = sys.argv[1:5]
s = open(spec).read()
assert s.count(old) == 1, 'the text to invert must occur exactly once'
out = os.path.join(os.path.dirname(spec), 'zz-inv-' + name + '.spec.ts')
open(out, 'w').write(s.replace(old, new).replace("from '../lib/", "from '../lib/").replace("test('", "test('INVERTED " + name + " — "))
print(out)
