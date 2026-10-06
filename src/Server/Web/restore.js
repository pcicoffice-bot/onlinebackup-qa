'use strict';
// WEB-010: the customer's restore from the web. The encryption password stays in this page's memory only: it is sent
// with each request (over the server's TLS) and is never stored in the browser.
(async function () {
  // I18N-010: the language — the one chosen here before, else the browser's, else the company's default
  let defLang = 'en';
  // BRAND-010: the company's brand (name, slogan, logo, colours) is public — shown before sign-in too
  const B = {};
  try { const x = await (await fetch('/api/brand')).text(); for (const m of x.matchAll(/<f n="brand(\w+)">([^<]*)</g)) B[m[1]] = m[2].replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"'); if (B.LANGUAGE) defLang = B.LANGUAGE; } catch (e) { }
  await I18N.load(I18N.pick(defLang));
  document.getElementById('langbox').append(I18N.selector());
  document.querySelectorAll('[data-t]').forEach((e) => { e.textContent = t(e.getAttribute('data-t')); });
  const S = { session: null, key: null, set: null, point: null, path: '', picked: new Set() };
  const $ = (sel) => document.querySelector(sel);
  function showBrand(name, slogan, logo, color, accent) {
    if (name) { $('#pname').textContent = name; document.title = t('Restore — {0}', name); }
    $('#slogan').textContent = slogan || '';
    if (logo && /^data:image\/(png|jpeg);base64,/.test(logo)) { $('#logo').src = logo; $('#logo').hidden = false; }
    if (color && /^#[0-9a-fA-F]{6}$/.test(color)) document.documentElement.style.setProperty('--brand', color);
    if (accent && /^#[0-9a-fA-F]{6}$/.test(accent)) document.documentElement.style.setProperty('--accent', accent);
  }
  showBrand(B.PRODUCT, B.SLOGAN, B.LOGO, B.COLOR, B.ACCENT);
  const main = $('#main');

  function h(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k === 'onclick' || k === 'onchange' || k === 'onsubmit') e.addEventListener(k.slice(2), v);
      else if (k === 'class') e.className = v;
      else if (k === 'style') e.style.cssText = v;   // CSSOM: allowed by the page's Content-Security-Policy (a style attribute is not)
      else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids.flat()) if (k !== null && k !== undefined && k !== false) e.append(k instanceof Node ? k : String(k));
    return e;
  }
  function toXml(obj) {
    const doc = document.implementation.createDocument(null, 'm', null);
    const build = (o, el) => {
      for (const [k, v] of Object.entries(o)) {
        if (Array.isArray(v)) {
          const l = doc.createElement('l'); l.setAttribute('n', k);
          for (const item of v) { const i = doc.createElement('i'); build(item, i); l.append(i); }
          el.append(l);
        } else if (v !== undefined && v !== null) { const f = doc.createElement('f'); f.setAttribute('n', k); f.textContent = String(v); el.append(f); }
      }
    };
    build(obj, doc.documentElement);
    return new XMLSerializer().serializeToString(doc);
  }
  function fromXml(el) {
    const o = {};
    for (const c of el.children) {
      if (c.tagName === 'f') o[c.getAttribute('n')] = c.textContent;
      else if (c.tagName === 'l') o[c.getAttribute('n')] = Array.from(c.children).map(fromXml);
    }
    return o;
  }
  const list = (v) => Array.isArray(v) ? v : [];
  async function call(method, path, body, raw) {
    const r = await fetch('/api/' + path, { method, headers: Object.assign({ 'Content-Type': 'application/xml' }, S.session ? { 'X-Session': S.session } : {}),
      body: method === 'GET' ? undefined : toXml(body || {}) });
    if (raw && r.ok) return r;
    const text = await r.text();
    let m = {}; try { m = text ? fromXml(new DOMParser().parseFromString(text, 'application/xml').documentElement) : {}; } catch (e) { }
    if (r.status === 401 && path !== 'login') { signOut(); throw new Error(tr(m.message) || t('Please sign in again.')); }
    if (!r.ok) throw new Error(tr(m.message) || t('Error {0}', r.status));
    return m;
  }
  function toast(msg, err) {
    const t = $('#toast'); t.textContent = msg; t.className = err ? 'err' : ''; t.hidden = false;
    clearTimeout(toast.t); toast.t = setTimeout(() => { t.hidden = true; }, err ? 7000 : 3500);
  }
  const wrap = (fn) => async (ev) => { if (ev && ev.preventDefault) ev.preventDefault(); try { await fn(ev); } catch (e) { toast(e.message, true); } };
  const size = (n) => { n = Number(n || 0); const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return (i ? n.toFixed(1) : n) + ' ' + u[i]; };
  const when = (t) => (t || '').replace('T', ' ').slice(0, 16);

  function signOut() { S.session = null; S.key = null; $('#logout').hidden = true; $('#who').textContent = ''; loginPage(); }
  $('#logout').addEventListener('click', signOut);

  function loginPage() {
    const login = h('input', { autocomplete: 'username', dir: 'ltr', required: true }), pw = h('input', { type: 'password', autocomplete: 'current-password', required: true });
    const otp = h('input', { inputmode: 'numeric', autocomplete: 'one-time-code', dir: 'ltr', placeholder: t('If two-step verification is set up') });
    main.replaceChildren(h('form', { class: 'card narrow', onsubmit: wrap(async () => {
      const r = await call('POST', 'login', { login: login.value.trim(), password: pw.value, otp: otp.value.trim() });
      S.session = r.session; $('#logout').hidden = false; await setsPage();
    }) }, h('div', { class: 'lbrand' }, B.LOGO && /^data:image\/(png|jpeg);base64,/.test(B.LOGO) ? h('img', { src: B.LOGO, alt: '' }) : h('span', { class: 'mark' }, '✦'),
      h('div', {}, h('b', {}, B.PRODUCT || ''), B.SLOGAN ? h('small', {}, B.SLOGAN) : null)), h('h2', {}, t('Restore files from the backup')), h('label', {}, t('User name')), login, h('label', {}, t('Password')), pw, h('label', {}, t('Verification code')), otp, h('button', { class: 'primary' }, t('Sign in'))));
    login.focus();
  }

  async function setsPage() {
    const r = await call('GET', 'webrestore/sets');
    $('#who').textContent = r.login || '';
    showBrand(r.product, r.slogan, r.logo, r.color, r.accent);   // the customer's reseller brand, once signed in
    const sets = list(r.sets);
    main.replaceChildren(h('div', { class: 'card' }, h('h2', {}, t('Choose a backup')),
      sets.length === 0 ? h('p', { class: 'muted' }, t('There are no backups in this account.')) : null,
      sets.map(s => h('div', { class: 'row' }, h('b', {}, s.name), ' ', h('span', { class: 'muted' }, s.type),
        s.web === '1' ? h('button', { onclick: wrap(() => keyPage(s)) }, t('Restore')) : h('span', { class: 'muted' }, t(' — restored from the backup software on the computer'))))));
  }

  function keyPage(s) {
    S.set = s; S.picked.clear();
    const key = h('input', { type: 'password', autocomplete: 'off', required: true });
    main.replaceChildren(h('form', { class: 'card narrow', onsubmit: wrap(async () => {
      const r = await call('POST', 'webrestore/' + s.id + '/points', { key: key.value });
      S.key = key.value; pointsPage(list(r.points));
    }) }, h('h2', {}, s.name), h('label', {}, t('The backup\'s encryption password')), key,
      h('p', { class: 'muted' }, t('The password is not stored on the server or in the browser; it is needed to decrypt the files.')),
      h('button', { class: 'primary' }, t('Continue')), h('button', { type: 'button', class: 'ghost', onclick: wrap(setsPage) }, t('Back'))));
    key.focus();
  }

  // AI-040: find a file in all the points — in plain words ("the Excel file Dana edited on Tuesday") or by name
  function searchBox(points) {
    const q = h('input', { placeholder: t('e.g. the Excel file Dana edited on Tuesday'), required: true, dir: 'auto' });
    return h('form', { class: 'row search', onsubmit: wrap(async () => {
      toast(t('Searching…'));
      const r = await call('POST', 'webrestore/' + S.set.id + '/search', { key: S.key, q: q.value, lang: I18N.lang });
      resultsPage(q.value, r, points);
    }) }, h('b', {}, t('🔎 Find a file')), q, h('button', { class: 'primary' }, t('Search')));
  }

  function resultsPage(query, r, points) {
    const files = list(r.files);
    main.replaceChildren(h('div', { class: 'card' }, h('h2', {}, S.set.name), searchBox(points),
      h('p', {}, r.ai === '1' ? ['🤖 ', r.explanation || ''] : t('Searching for file names that contain every word.')),
      files.length === 0 ? h('p', { class: 'muted' }, t('Nothing was found. Try other words.')) : h('p', { class: 'muted' }, t('{0} files found', files.length)),
      h('table', { class: 'found' }, h('tbody', {}, files.map(f => {
        const parent = f.path.slice(0, f.path.lastIndexOf('/'));
        return h('tr', {}, h('td', {}, h('b', {}, f.name), h('div', { class: 'muted', dir: 'ltr' }, parent)),
          h('td', { class: 'muted', dir: 'ltr' }, size(f.size)), h('td', { dir: 'ltr' }, when(f.mtime)),
          h('td', {}, h('span', { class: 'muted' }, t('Restore point: ')), h('span', { dir: 'ltr' }, when(f.pointTime))),
          h('td', {}, h('button', { onclick: wrap(async () => { S.point = f.point; S.pointTime = f.pointTime; S.picked = new Set([f.path]); await download(); }) }, t('⬇ Download')), ' ',
            h('button', { class: 'ghost', onclick: wrap(() => { S.point = f.point; S.pointTime = f.pointTime; S.picked = new Set([f.path]); return browse(parent); }) }, t('Open folder'))));
      }))),
      h('button', { class: 'ghost', onclick: () => pointsPage(points) }, t('Back'))));
  }

  function pointsPage(points) {
    main.replaceChildren(h('div', { class: 'card' }, h('h2', {}, t('{0} — restore points', S.set.name)),
      points.length ? searchBox(points) : null,
      points.length === 0 ? h('p', { class: 'muted' }, t('There are no restore points yet.')) : null,
      points.map(p => h('div', { class: 'row' }, h('span', { dir: 'ltr' }, when(p.time)), ' ', h('button', { onclick: wrap(() => { S.point = p.id; S.pointTime = p.time; S.picked.clear(); browse(''); }) }, t('Browse')))),
      h('button', { class: 'ghost', onclick: wrap(setsPage) }, t('Back'))));
  }

  async function browse(path) {
    S.path = path;
    const r = await call('POST', 'webrestore/' + S.set.id + '/ls', { key: S.key, point: S.point, path });
    const files = list(r.files).sort((a, b) => (a.type === 'dir' ? 0 : 1) - (b.type === 'dir' ? 0 : 1) || a.name.localeCompare(b.name));
    const crumbs = [h('a', { href: '#', onclick: wrap(() => browse('')) }, t('Top'))];
    let acc = '';
    for (const part of path.split('/').filter(x => x && x !== '/')) { acc += '/' + part; const to = acc; crumbs.push(' / ', h('a', { href: '#', onclick: wrap(() => browse(to)) }, part)); }
    const count = h('span', { class: 'muted' });
    const upd = () => { count.textContent = S.picked.size ? t('Selected: {0}', S.picked.size) : ''; };
    upd();
    main.replaceChildren(h('div', { class: 'card' }, h('h2', {}, S.set.name), h('div', { class: 'muted' }, t('Restore point: '), h('span', { dir: 'ltr' }, when(S.pointTime))), h('div', {}, crumbs),
      h('table', {}, h('tbody', {}, files.map(f => {
        const cb = h('input', { type: 'checkbox', onchange: () => { cb.checked ? S.picked.add(f.path) : S.picked.delete(f.path); upd(); } });
        cb.checked = S.picked.has(f.path);
        return h('tr', {}, h('td', {}, cb), h('td', {}, f.type === 'dir' ? h('a', { href: '#', onclick: wrap(() => browse(f.path)) }, '📁 ' + f.name) : f.name),
          h('td', { class: 'muted', dir: 'ltr' }, f.type === 'dir' ? '' : size(f.size)), h('td', { class: 'muted', dir: 'ltr' }, when(f.mtime)));
      }))),
      h('div', { class: 'row' }, h('button', { class: 'primary', onclick: wrap(download) }, t('⬇ Download selected (ZIP)')), ' ', count, ' ',
        h('button', { class: 'ghost', onclick: wrap(() => keyPage(S.set)) }, t('Another point')))));
  }

  async function download() {
    if (S.picked.size === 0) throw new Error(t('Select files or folders.'));
    toast(t('Preparing the files…'));
    const r = await call('POST', 'webrestore/' + S.set.id + '/download', { key: S.key, point: S.point, paths: Array.from(S.picked).map(p => ({ p })) }, true);
    const a = h('a', { href: URL.createObjectURL(await r.blob()), download: 'restore-' + S.point + '.zip' });
    document.body.append(a); a.click(); a.remove();
    toast(t('The files were downloaded.'));
  }

  loginPage();
})();
