#!/usr/bin/env python3
"""The run's request as step outputs: the inputs of a manual run, or tools/qa-shards/request.json (push of qa-shards-*)."""
import json, os, sys
ev = os.environ.get('EVENT', '')
r = json.loads(os.environ.get('INPUTS') or '{}') if ev == 'workflow_dispatch' else json.load(open('tools/qa-shards/request.json'))
os_ = r.get('os', 'ubuntu-latest')
if os_ not in ('ubuntu-latest', 'windows-latest'): sys.exit('request: os must be ubuntu-latest or windows-latest')
out = {'os': os_, 'select': str(r.get('select', '')), 'shards': int(r.get('shards', 4)), 'repeat': int(r.get('repeat', 1)),
       'max_parallel': max(1, min(int(r.get('max_parallel', 6)), 18)), 'control': 'true' if str(r.get('control', False)).lower() == 'true' else 'false'}
if not (1 <= out['shards'] <= 64 and 1 <= out['repeat'] <= 50): sys.exit('request: shards 1-64, repeat 1-50')
for c in out['select']:
    if not (c.isalnum() or c in ' _.'): sys.exit('request: select may hold only letters, digits, _ . and spaces')
out['title'] = "%s, select '%s', %d shards x %d%s" % (out['os'], out['select'], out['shards'], out['repeat'], ', serial control' if out['control'] == 'true' else '')
for k, v in out.items(): print('%s=%s' % (k, v))
