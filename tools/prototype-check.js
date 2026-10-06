// PROTO-020: the prototype's own robot — every page, computer and phone, light and dark: errors, sideways scrolling, broken words.
//   NODE_PATH=$(npm root -g) node tools/prototype-check.js <outDir>
const { chromium } = require('playwright'); const fs = require('fs');
(async () => {
  const OUT = process.argv[2]; fs.mkdirSync(OUT, { recursive: true });
  try { const src = fs.readFileSync('prototype/index.html', 'utf8'); new Function(src.slice(src.lastIndexOf('<script>') + 8, src.lastIndexOf('</script>'))); } catch (e) { console.log('SCRIPT SYNTAX ERROR: ' + e.message); process.exit(1); }
  const html = '<!doctype html><html><head><meta charset="utf-8"></head><body>' + fs.readFileSync('prototype/index.html', 'utf8') + '</body></html>';
  fs.writeFileSync(OUT + '/p.html', html);
  const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium' }); const probs = [];
  const pages = ['dash','allsets','tasks','live','cust','customer','alerts','ai','logs','reports','restore','storage','dest','license','resellers','policies','notify','security','time','integr','contract','brand','updates','tickets','techs','tset'];
  const ctabs = ['overview','pcs','sets','contacts','quota','sec','reports','logs','calls'];
  const stabs = ['general','sched','method','dest','ret','filter','enc','perf','cmd','drill','maint','ai'];
  const cl = ['status','sets','restore','settings','security','logs','help','register'];
  const states = [...pages.map((p) => ({ app: 'server', page: p })), ...ctabs.map((t) => ({ app: 'server', page: 'customer', ctab: t, set: null })), ...stabs.map((t) => ({ app: 'server', page: 'customer', ctab: 'sets', set: 0, stab: t })), ...cl.map((t) => ({ app: 'client', page2: t, set: null })), { app: 'server', page: 'tickets', tck: 4471 }, { app: 'server', page: 'tickets', tck: -1 }, { app: 'server', page: 'tickets', tck: null, tv: 'הכול' }, { app: 'server', page: 'techs', tech: 1 }, { app: 'server', page: 'techs', tech: null }, ...[0,1,2,3,4,5].map((n) => ({ app: 'setup', page3: 'srv', wz: n })), ...[0,1,2,3].map((n) => ({ app: 'setup', page3: 'cli', wc: n })), { app: 'setup', page3: 'login' }, { app: 'setup', page3: 'web' }, { app: 'client', page2: 'security', qr2: true }];
  for (const [vp, w] of [['desktop', 1366], ['phone', 390]]) for (const dark of [false, true]) {
    const ctx = await b.newContext({ viewport: { width: w, height: 900 }, colorScheme: dark ? 'dark' : 'light' }); const p = await ctx.newPage();
    const errs = []; p.on('pageerror', (e) => errs.push(e.message));
    for (const st of states) {
      await p.goto('file://' + require('path').resolve(OUT + '/p.html'));
      await p.evaluate((s) => { localStorage.clear(); Object.assign(S, s); render(); }, st);
      if (st.page === 'tasks') await p.evaluate(() => { S.task = 9; render(); });
      if (st.page === 'dash' || st.page2 === 'status') await p.evaluate(() => { S.edit = true; render(); });
      const id = [vp, dark ? 'dark' : 'light', st.app, st.page || '', st.ctab || '', st.stab || st.page2 || ''].join('_');
      const r = await p.evaluate(() => { const o = []; if (document.documentElement.dir !== 'rtl') o.push('not rtl'); if (getComputedStyle(document.querySelector('.rail') || document.body).order === 'x') o.push(''); if (document.documentElement.scrollWidth > innerWidth + 2) o.push('scrolls sideways ' + document.documentElement.scrollWidth);
        for (const e of document.querySelectorAll('h1,h2,h3,b,span,td,button,p,label')) { const rc = e.getBoundingClientRect(); if (!rc.width) continue;
          const lh = parseFloat(getComputedStyle(e).lineHeight) || parseFloat(getComputedStyle(e).fontSize) * 1.3;
          if (rc.height > lh * 1.6 && !e.closest('.ltr,.num,code,textarea')) for (const n of e.childNodes) if (n.nodeType === 3) { const re = /[\p{L}\p{N}]{4,}/gu; let m; while ((m = re.exec(n.textContent))) { const g = document.createRange(); g.setStart(n, m.index); g.setEnd(n, m.index + m[0].length); if (new Set(Array.from(g.getClientRects()).filter((q) => q.width > 0).map((q) => Math.round(q.top))).size > 1) { o.push('broken "' + m[0] + '"'); break; } } } }
        return [...new Set(o)]; });
      if (errs.length) r.push(...errs.splice(0));
      if (r.length) { probs.push(id + ': ' + r.join(' | ')); await p.screenshot({ path: OUT + '/' + id + '.png', fullPage: true }); }
      if (!dark && ['dash','cust','customer','license','storage'].includes(st.page) && !st.ctab || (st.stab === 'dest' || st.stab === 'sched' || st.page2 === 'status' || st.page2 === 'restore')) await p.screenshot({ path: OUT + '/shot_' + id + '.png', fullPage: true });
    }
    await ctx.close();
  }
  await b.close(); console.log(probs.length ? probs.join('\n') : 'no problems'); console.log(states.length * 4 + ' screens');
})();
