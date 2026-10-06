#!/usr/bin/env python3
"""
The Windows QA contact sheet: python3 contact-sheet.py <win-out>  ->  <win-out>/index.html
Every journey of the run in order: its verdicts kept apart (FUNCTIONAL = the action worked, proven from outside;
VISUAL = the screens look right; UX = the flow is clear, by written checks), its steps with what was expected and
what was seen, its screens one after the other as the robot saw them, and the visual / UX findings with their
screenshots. Only relative links: the folder opens as it is, from the artifact or from the branch qa-evidence.
"""
import html, json, os, sys

def load(p, default=None):
    try:
        with open(p, encoding='utf-8-sig') as f: return json.load(f)
    except Exception: return default

def main(root):
    # every phase's summary, at the top and in sub-folders (the VM's phases: before and after each restart)
    phases = []
    for dp, dn, fn in sorted(os.walk(root)):
        for f in sorted(fn):
            if f.startswith('windows-') and f.endswith('.json'):
                d = load(os.path.join(dp, f))
                if d: phases.append((os.path.relpath(dp, root), d))
    facts = [p.get('facts', {}) for _, p in phases]
    rows = []
    for rel, p in phases:
        for j in p.get('journeys') or []:
            j = dict(j); pre = '' if rel == '.' else rel.replace(os.sep, '/') + '/'
            j['path'] = pre + j['id']; j['anchor'] = (pre + j['id']).replace('/', '-'); j['label'] = (pre.rstrip('/').split('/')[-1] + ' ' if pre else '') + j['id']
            rows.append(j)
    E = html.escape
    badge = lambda v: '<span class="b %s">%s</span>' % ({'PASS': 'ok', 'FAIL': 'bad'}.get(v, 'nt'), E(v or 'NOT TESTED'))
    out = ['<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Windows QA</title>',
           '<style>body{font:14px Segoe UI,system-ui,sans-serif;margin:16px;color:#0f172a;background:#f8fafc}h1{font-size:22px}h2{font-size:18px;margin-top:36px;border-top:1px solid #cbd5e1;padding-top:14px}'
           'table{border-collapse:collapse;margin:8px 0;background:#fff}td,th{border:1px solid #e2e8f0;padding:4px 8px;vertical-align:top;text-align:left}th{background:#f1f5f9}'
           '.b{display:inline-block;padding:1px 8px;border-radius:9px;font-weight:600;font-size:12px}.ok{background:#dcfce7;color:#166534}.bad{background:#fee2e2;color:#991b1b}.nt{background:#e2e8f0;color:#334155}'
           '.strip{display:flex;flex-wrap:wrap;gap:10px;align-items:flex-start}.strip figure{margin:0;width:260px;background:#fff;border:1px solid #e2e8f0;padding:6px}.strip img{width:100%;display:block}'
           'figcaption{font-size:12px;margin-top:4px}.arrow{align-self:center;font-size:20px;color:#64748b}.small{font-size:12px;color:#475569}td.actual{max-width:520px;word-break:break-word}</style>',
           '<h1>Windows QA - what the robot did and saw</h1>']
    for f in facts:
        out.append('<p class="small">%s</p>' % E(' | '.join('%s: %s' % (k, v) for k, v in f.items())))
    out.append('<p class="small">FUNCTIONAL: the action worked, proven from outside the product (Windows, the server, SHA-256). VISUAL: no visual finding of severity Medium or High on its screens. UX: every written flow check passed. A screenshot never proves a backup: only the SHA-256 comparison does.</p>')
    out.append('<table><tr><th>Journey</th><th>FUNCTIONAL</th><th>VISUAL</th><th>UX</th><th>Why not PASS</th></tr>')
    for r in rows:
        out.append('<tr><td><a href="#%s">%s %s</a></td><td>%s</td><td>%s</td><td>%s</td><td class="actual">%s</td></tr>' % (E(r['anchor']), E(r['label']), E(r['title']), badge(r.get('functional')), badge(r.get('visual')), badge(r.get('ux')), E((r.get('reason') or '')[:600])))
    out.append('</table>')
    allf = []
    for r in rows:
        d = os.path.join(root, r['path']); j = load(os.path.join(d, 'journey.json'), {}) or {}
        out.append('<h2 id="%s">%s %s &nbsp; %s %s %s</h2>' % (E(r['anchor']), E(r['label']), E(r['title']), badge(r.get('functional')), badge(r.get('visual')), badge(r.get('ux'))))
        shots = sorted(f for f in (os.listdir(d) if os.path.isdir(d) else []) if f.endswith('.png') and not f.endswith('.window.png'))
        if shots:
            out.append('<div class="strip">')
            for i, s in enumerate(shots):
                win = s[:-4] + '.window.png'
                link = '%s/%s' % (r['path'], win if os.path.exists(os.path.join(d, win)) else s)
                if i: out.append('<span class="arrow">&rarr;</span>')
                out.append('<figure><a href="%s/%s"><img loading="lazy" src="%s"></a><figcaption>%s<br><a href="%s">window only</a></figcaption></figure>' % (E(r['path']), E(s), E(link), E(s[:-4]), E(link)))
            out.append('</div>')
        steps = j.get('steps') or []
        if steps:
            out.append('<table><tr><th>Step</th><th>Result</th><th>Expected</th><th>Actual (what the oracle saw)</th><th>Screen</th></tr>')
            for s in steps:
                shot = s.get('screenshot') or ''
                out.append('<tr><td>%s</td><td>%s</td><td>%s</td><td class="actual">%s</td><td>%s</td></tr>' % (E(s.get('step', '')), badge(s.get('result')) if s.get('result') != 'INFO' else 'info', E(s.get('expected', '')), E(str(s.get('actual', ''))[:1500]), ('<a href="%s/%s">%s</a>' % (E(r['path']), E(shot), E(shot))) if shot.endswith('.png') else ''))
            for s in j.get('ux') or []:
                out.append('<tr><td>%s</td><td>%s</td><td>%s</td><td class="actual">%s</td><td></td></tr>' % (E(s.get('step', '')), badge(s.get('result')), E(s.get('expected', '')), E(str(s.get('actual', ''))[:800])))
            out.append('</table>')
        allf += [dict(f, journey=r['path']) for f in (j.get('visual') or [])]
        ev = [x for x in ('windows-state.json', 'event-log.txt', 'service-events.txt', 'install-server.txt') if os.path.exists(os.path.join(d, x))] + [x for x in ('agent-logs', 'server-logs') if os.path.isdir(os.path.join(d, x))] + sorted(x for x in os.listdir(d) if x.endswith(('.sha256', 'differences.txt'))) if os.path.isdir(d) else []
        if ev: out.append('<p class="small">Evidence: %s</p>' % ' &middot; '.join('<a href="%s/%s">%s</a>' % (E(r['path']), E(x), E(x)) for x in ev))
    out.append('<h2 id="findings">Visual and UX findings (%d)</h2>' % len(allf))
    out.append('<p class="small">Findings are for a person to judge with the screenshot. A change of the screens\' flow needs the owner\'s approval first.</p>')
    out.append('<table><tr><th>Journey</th><th>Kind</th><th>Severity</th><th>Screen</th><th>Problem</th><th>Expected</th><th>Actual</th><th>Recommendation</th><th>Screenshot</th></tr>')
    order = {'High': 0, 'Medium': 1, 'Low': 2}
    for f in sorted(allf, key=lambda f: (order.get(f.get('severity'), 3), f['journey'])):
        shot = f.get('screenshot') or ''
        img = ('<a href="%s/%s"><img src="%s/%s" width="220"></a>' % (E(f['journey']), E(shot), E(f['journey']), E(shot))) if shot.endswith('.png') else ''
        cells = ''.join('<td>%s</td>' % E(str(f.get(k) or '')) for k in ('journey', 'kind', 'severity', 'screen', 'problem', 'expected', 'actual', 'recommendation'))
        out.append('<tr>%s<td>%s</td></tr>' % (cells, img))
    out.append('</table>')
    with open(os.path.join(root, 'index.html'), 'w', encoding='utf-8') as fh: fh.write('\n'.join(out))
    print(os.path.join(root, 'index.html'), len(rows), 'journeys', len(allf), 'findings')

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'win-out')
