'use strict';
// CONSOLE-010: the admin site of the backup server — the approved prototype (prototype/index.html), on the real data of
// /api/admin. Every text is English here and translated by /i18n/<lang>.json (I18N-010). Editors end with "Save and exit"
// / "Exit without saving". No inline style attributes (Content-Security-Policy): styles through the CSSOM only.
(async function () {
  let defLang = 'en';
  try { const m = /<f n="brandLANGUAGE">([^<]*)</.exec(await (await fetch('/api/brand')).text()); if (m) defLang = m[1]; } catch (e) { }
  await I18N.load(I18N.pick(defLang));
  const $ = (sel, root) => (root || document).querySelector(sel);
  // SEC-110: on the server itself the sign-in is remembered in this browser (localStorage); elsewhere only for the tab
  const kept = () => { try { return sessionStorage.getItem('obAdmin') || localStorage.getItem('obAdminLocal'); } catch (e) { return null; } };
  const S = Object.assign({ session: kept() || null, page: 'dash' }, (() => { try { return JSON.parse(sessionStorage.getItem('obNav') || '{}'); } catch (e) { return {}; } })());
  S.session = kept() || null;
  const main = $('#main');

  // ------------------------------------------------------------------ building blocks
  function h(tag, a, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(a || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k.startsWith('on') && typeof v === 'function') e.addEventListener(k.slice(2), v);
      else if (k === 'class') e.className = v;
      else if (k === 'style') e.style.cssText = v;   // CSSOM: allowed by the CSP (a style attribute is not)
      else if (k === 'value' || k === 'checked' || k === 'selected' || k === 'disabled') e[k] = v;
      else e.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids.flat(Infinity)) if (k !== null && k !== undefined && k !== false) e.append(k instanceof Node ? k : String(k));
    return e;
  }
  const list = (v) => Array.isArray(v) ? v : [];
  const N = (v) => h('span', { class: 'num' }, v);
  // left-to-right text (paths, addresses); a long path may wrap after its \ or /, never inside a name
  const ltr = (v) => h('span', { class: 'ltr' }, ...(typeof v === 'string' && /[\\/]/.test(v) ? v.split(/(?<=[\\/])/).flatMap((x, i) => (i ? [h('wbr', {}), x] : [x])) : [v]));
  const pill = (cls, txt) => h('span', { class: 'pill ' + cls }, txt);
  const card = (title, extra, ...body) => h('section', { class: 'card' }, title ? h('div', { class: 'ch' }, h('h2', {}, title), extra || null) : null, h('div', { class: 'cb' }, ...body));
  const fr = (label, hint, ...inputs) => h('div', { class: 'fr' }, h('div', { class: 'lb' }, h('b', {}, label), hint ? h('span', {}, hint) : null), h('div', { class: 'in' }, ...inputs));
  const inp = (v, a) => h('input', Object.assign({ type: 'text', value: v == null ? '' : String(v) }, a || {}));
  const num = (v, a) => h('input', Object.assign({ type: 'number', value: v == null ? '' : String(v), class: 'w-auto', style: 'max-width:120px' }, a || {}));
  const sel = (opts, cur) => h('select', {}, opts.map((o) => Array.isArray(o) ? h('option', { value: o[0], selected: String(o[0]) === String(cur) }, o[1]) : h('option', { value: o, selected: o === cur }, o)));
  const tog = (on, label) => { const i = h('input', { type: 'checkbox', checked: !!on, 'aria-label': label || '' }); const l = h('label', { class: 'tog' }, i, h('span')); l.input = i; return l; };
  const togRow = (on, text) => { const tg = tog(on, text); const r = h('div', { class: 'inline' }, tg, h('span', {}, text)); r.input = tg.input; return r; };
  const head = (title, sub, acts, crumbs) => h('div', {}, crumbs ? h('div', { class: 'crumbs' }, crumbs) : null, h('div', { class: 'head' }, h('div', { class: 't' }, h('h1', {}, title), sub ? h('p', {}, sub) : null), acts ? h('div', { class: 'acts' }, acts) : null));
  const btn = (text, onclick, cls) => h('button', { class: 'btn ' + (cls || ''), type: 'button', onclick: wrap(onclick) }, text);
  function table(cols, rows, onRow, opts) {
    if (!rows.length) return h('div', { class: 'empty' }, (opts && opts.empty) || t('Nothing here yet.'));
    return h('div', { class: 'tw' }, h('table', { class: 'cards' }, h('thead', {}, h('tr', {}, cols.map((c) => h('th', {}, c)))),
      h('tbody', {}, rows.map((r, i) => h('tr', { class: onRow ? 'click' : '', onclick: onRow ? () => onRow(i) : null }, r.map((c, j) => h('td', { 'data-l': cols[j] || '' }, c)))))));
  }
  const bar = (pct, cls) => h('div', { class: 'bar ' + (cls || (pct >= 90 ? 'bad' : pct >= 75 ? 'warn' : '')) }, h('i', { style: 'width:' + Math.max(0, Math.min(100, pct)) + '%' }));
  const meter = (label, used, total, fmt) => { const p = total > 0 ? Math.round(used / total * 100) : 0; return h('div', { class: 'meter' }, h('div', { class: 'row' }, h('span', {}, label), h('span', {}, N((fmt ? fmt(used) : used) + (total > 0 ? ' / ' + (fmt ? fmt(total) : total) : '')), total > 0 ? ' ' : null, total > 0 ? h('span', { class: 'muted' }, '(' + p + '%)') : null)), total > 0 ? bar(p) : null); };
  function size(b) { b = Number(b || 0); const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; } return (i === 0 ? b.toFixed(0) : b.toFixed(b >= 100 ? 0 : 1)) + ' ' + u[i]; }
  function when(ms) { ms = Number(ms || 0); if (!ms || ms < 0) return '—'; const d = new Date(ms), p = (n) => String(n).padStart(2, '0'); return p(d.getDate()) + '/' + p(d.getMonth() + 1) + '/' + d.getFullYear() + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()); }
  const whenIso = (iso) => iso ? when(Date.parse(iso)) : '—';
  // R1: "Last backup" is the last run that backed up; when the last run after it failed, it says so in red (never a quiet date)
  function lastCell(s) {
    const r = s.lastResult || '', ok = !r || r === 'BS_STOP_SUCCESS', warn = r === 'BS_STOP_SUCCESS_WITH_WARNING', stop = r === 'BS_STOP_BY_USER';
    if (ok || warn || stop) return h('span', { class: 'lastrun' }, N(when(s.lastBackup)), warn ? ' ' : null, warn ? pill('warn', t('Warnings')) : null);
    return h('span', { class: 'lastrun' }, N(when(s.lastBackup)), ' ', pill('bad', r === 'BS_STOP_SUCCESS_WITH_ERROR' ? t('Last run: some data not backed up') : t('Last run failed {0}', when(s.lastResultTime))));
  }
  function toast(msg, err) { const e = $('#toast'); e.textContent = msg; e.className = err ? 'err' : ''; e.hidden = false; clearTimeout(toast.t); toast.t = setTimeout(() => { e.hidden = true; }, err ? 7000 : 3200); }
  const wrap = (fn) => async (e) => { try { await fn(e); } catch (x) { toast(x.message, true); } };
  const tr = (m) => m ? t(m) : m;
  function dialog(title, body, onSave, saveText) {
    const d = h('dialog', {}, h('h3', {}, title), body, h('div', { class: 'actions' },
      onSave ? h('button', { class: 'btn pri', onclick: wrap(async () => { if (await onSave() === false) return; d.close(); d.remove(); }) }, saveText || t('Save and exit')) : null,
      h('button', { class: 'btn', onclick: () => { d.close(); d.remove(); } }, onSave ? t('Exit without saving') : t('Close'))));
    document.body.append(d); d.showModal(); return d;
  }
  const confirmBox = (text) => new Promise((res) => { const d = dialog(t('Are you sure?'), h('p', {}, text), async () => { res(true); }, t('Yes')); d.addEventListener('close', () => res(false)); });
  // "Save and exit" / "Exit without saving" at the end of every editor
  const savebar = (save, exit, note) => h('div', { class: 'savebar' }, h('button', { class: 'btn pri', type: 'button', onclick: wrap(async () => { if (await save() === false) return; toast(t('Saved')); exit(); }) }, t('Save and exit')),
    h('button', { class: 'btn', type: 'button', onclick: () => exit() }, t('Exit without saving')), h('span', { class: 'sp' }), note ? h('span', { class: 'muted small' }, note) : null);

  // ------------------------------------------------------------------ XML messages (Core.Msg)
  function toXml(obj) {
    const doc = document.implementation.createDocument(null, 'm', null);
    const build = (o, el) => { for (const [k, v] of Object.entries(o)) { if (Array.isArray(v)) { const l = doc.createElement('l'); l.setAttribute('n', k); for (const item of v) { const i = doc.createElement('i'); build(item, i); l.append(i); } el.append(l); } else if (v !== undefined && v !== null) { const f = doc.createElement('f'); f.setAttribute('n', k); f.textContent = String(v); el.append(f); } } };
    build(obj, doc.documentElement); return new XMLSerializer().serializeToString(doc);
  }
  function fromXml(el) { const o = {}; for (const c of el.children) { if (c.tagName === 'f') o[c.getAttribute('n')] = c.textContent; else if (c.tagName === 'l') o[c.getAttribute('n')] = Array.from(c.children).map(fromXml); } return o; }
  async function api(method, path, body) {
    const r = await fetch('/api/admin/' + path, { method, headers: Object.assign({ 'Content-Type': 'application/xml' }, S.session ? { 'X-Session': S.session } : {}), body: body ? toXml(body) : (method === 'GET' ? undefined : toXml({})) });
    const text = await r.text();
    const m = text ? fromXml(new DOMParser().parseFromString(text, 'application/xml').documentElement) : {};
    if (r.status === 401 && path !== 'login') { logout(); throw new Error(tr(m.message) || t('Please sign in again.')); }
    if (r.status === 403 && m.error === 'TOTP_ENROLL') { enrollPage(); throw new Error(tr(m.message)); }
    if (!r.ok) throw new Error(tr(m.message) || t('Error {0}', r.status));
    return m;
  }
  const enc = encodeURIComponent;

  // ------------------------------------------------------------------ brand, sign-in, two-step
  function brand(s) {
    if (!s) return;
    if (s.brandPRODUCT) { S.brand = Object.assign(S.brand || {}, s); document.title = s.brandPRODUCT; }
    const b = S.brand || {};
    const top = $('#top'); top.replaceChildren(...[
      h('div', { class: 'brand' }, b.brandLOGO && /^data:image\/(png|jpeg);base64,/.test(b.brandLOGO) ? h('img', { src: b.brandLOGO, alt: '' }) : h('i', { class: 'mk' }, '✦'),
        h('div', {}, b.brandPRODUCT || 'ITSguard Server Online', h('small', {}, b.brandSLOGAN || t('Backup server')))),
      I18N.selector(),
      S.me ? h('span', { class: 'who' }, (S.me.admin || '') + (S.vendor ? ' · ' + S.vendor : '')) : null,
      S.me && S.update && S.update.available === '1' ? h('button', { class: 'tb upd', type: 'button', onclick: updateNow, title: t('Version {0} is installed', S.update.current) }, '⬆ ' + t('Update to {0}', S.update.latest))
        : S.me && S.update ? h('button', { class: 'tb', type: 'button', title: t('Check for updates'), onclick: async () => {
          await checkUpdate(true); const u = S.update || {};
          if (u.available === '1') updateNow(); else updateFromFiles(u);
        } }, '⟳ ' + t('Version {0}', S.update.current)) : null,
      S.me ? h('button', { class: 'tb', type: 'button', onclick: fullScreen }, '⛶ ' + t('Full screen')) : null,
      S.me ? h('button', { class: 'tb', type: 'button', onclick: logout }, t('Sign out')) : null].filter(Boolean));
    if (b.brandCOLOR && /^#[0-9a-fA-F]{6}$/.test(b.brandCOLOR)) document.documentElement.style.setProperty('--brand', b.brandCOLOR);
    if (b.brandACCENT && /^#[0-9a-fA-F]{6}$/.test(b.brandACCENT)) document.documentElement.style.setProperty('--accent', b.brandACCENT);
  }
  // UPD-010: the newest version from the software vendor (signed by the vendor), installed by itself — data and settings kept
  async function checkUpdate(force) {
    if (!S.me || S.vendor) return;
    try { S.update = await api('GET', 'update' + (force ? '?check=1' : '')); } catch (e) { S.update = null; }
    brand(S.brand || {});
  }
  // UPD-030 (owner: "everything through the Update button"): the update files picked here, on the server itself
  function updateFromFiles(u) {
    // UPD-050 (owner: "Update does nothing"): one clear window — the state on top, the automatic updates with one Save, and
    // the update from files folded away (only for files received); its own button works only once files are chosen
    const repo = inp('', { class: 'ltr', placeholder: 'pcicoffice-bot/golan-crm@updates' }), key = h('input', { type: 'password', class: 'ltr', autocomplete: 'off' }), auto = togRow(false, t('Install new versions by themselves at night (02:00–05:00)'));
    const keyState = h('span', { class: 'muted small' });
    api('GET', 'update/source').then((x) => { repo.value = x.repo || 'pcicoffice-bot/golan-crm@updates'; key.placeholder = x.hasToken === '1' ? t('(saved — empty = no change)') : 'github_pat_…'; keyState.textContent = x.hasToken === '1' ? '✓ ' + t('A key is saved') : t('No key saved yet — paste it here'); auto.input.checked = x.auto === '1'; }).catch(() => { });
    const newest = u.source === '1' && !u.message;
    const status = h('div', { class: 'note ' + (newest ? 'ok' : '') }, newest ? '✓ ' + t('The newest version is installed ({0}).', u.current) : t('Version {0} is installed', u.current));
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname);
    const pick = h('input', { type: 'file', multiple: true, accept: '.bin,.zip' });
    const install = btn(t('Install the files'), async () => {
      const files = [...pick.files].sort((a, b) => a.name.localeCompare(b.name, 'en', { numeric: true }));
      if (!files.length) throw new Error(t('Choose the update files.'));
      const r = await fetch('/api/admin/update/upload', { method: 'POST', headers: { 'X-Session': S.session, 'Content-Type': 'application/octet-stream' }, body: new Blob(files) });
      if (!r.ok) { let m = t('Error {0}', r.status); try { m = tr(fromXml(new DOMParser().parseFromString(await r.text(), 'application/xml').documentElement).message) || m; } catch (e) { } throw new Error(m); }
      toast(t('Updating… the page reloads by itself when the server is back.'));
      const wait = async () => { try { const x = await fetch('/api/brand', { cache: 'no-store' }); if (x.ok && (await api('GET', 'update')).state !== 'installing') { location.reload(); return; } } catch (e) { } setTimeout(wait, 3000); };
      setTimeout(wait, 8000);
    }, 'sm');
    install.disabled = true; pick.addEventListener('change', () => { install.disabled = !pick.files.length; });
    const files = h('details', {}, h('summary', {}, t('Update from files (only if you received update files)')),
      local ? h('div', { class: 'stack' }, h('p', { class: 'muted small' }, t('Choose all the update files you received — the server joins them, checks them and updates itself. The data and settings are kept.')), pick, install)
        : h('p', { class: 'muted small' }, t('An update from files is done on the server itself: open https://localhost:8443/admin there.')));
    dialog(t('Update'), h('div', { class: 'stack' }, status,
      h('h3', {}, t('Automatic updates')), h('p', { class: 'muted small' }, t('Your private update store on GitHub and its read-only key. The server checks it by itself.')),
      h('label', {}, t('Repository')), repo, h('label', {}, t('Read-only key')), key, keyState, auto, files),
      async () => { await api('POST', 'update/source', { repo: repo.value, token: key.value || undefined, auto: auto.input.checked ? 1 : 0 }); toast(t('Saved')); await checkUpdate(true); });
  }
  function updateNow() {
    const u = S.update || {};
    dialog(t('Update to version {0}', u.latest), h('div', { class: 'stack' }, h('p', {}, t('Installed now: {0}', u.current)),
      h('p', {}, t('The server stops for about a minute and starts again with the new version. The data, the settings and the backups are kept; a backup running now goes on after the restart.'))),
      async () => {
        await api('POST', 'update');
        toast(t('Updating… the page reloads by itself when the server is back.'));
        let down = false;
        const tick = async () => {
          try {
            const st = await api('GET', 'update');
            if (st.state === 'failed') { toast(tr(st.message) || t('The update failed'), true); return; }
          } catch (e) { down = true; }
          if (down) { try { const r = await fetch('/api/brand', { cache: 'no-store' }); if (r.ok) { location.reload(); return; } } catch (e) { } }
          setTimeout(tick, 3000);
        };
        setTimeout(tick, 3000);
      }, t('Update now'));
  }
  async function fullScreen() { try { if (document.fullscreenElement) await document.exitFullscreen(); else await document.documentElement.requestFullscreen(); } catch (e) { toast(t('The browser does not allow full screen here. Press F11.')); } }
  const loginBrand = () => { const b = S.brand || {}; return h('div', { class: 'lbrand' }, b.brandLOGO && /^data:image\/(png|jpeg);base64,/.test(b.brandLOGO) ? h('img', { src: b.brandLOGO, alt: '' }) : h('i', { class: 'mk' }, '✦'), h('div', {}, h('b', {}, b.brandPRODUCT || 'ITSguard Server Online'), b.brandSLOGAN ? h('small', {}, b.brandSLOGAN) : null)); };
  // AUTH-020 (owner: "explain the code field with a picture — it is Google two-step; the whole page needs a new design"):
  // a two-panel sign-in — the product on one side (a quiet drawing), the form on the other; under the code a small phone
  // shows where the six digits come from
  const SVG_NS = 'http://www.w3.org/2000/svg';
  function svgFrom(markup) { const d = new DOMParser().parseFromString('<svg xmlns="' + SVG_NS + '">' + markup + '</svg>', 'image/svg+xml').documentElement; return document.importNode(d, true); }
  function authArt() {
    const g = svgFrom('<defs><linearGradient id="lg1" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".95"/><stop offset="1" stop-color="#fff" stop-opacity=".55"/></linearGradient></defs>'
      + '<circle cx="150" cy="118" r="92" fill="#fff" fill-opacity=".06"/><circle cx="150" cy="118" r="64" fill="#fff" fill-opacity=".07"/>'
      + '<path d="M96 132a30 30 0 0 1 8-59 40 40 0 0 1 76-6 28 28 0 0 1 24 54z" fill="url(#lg1)"/>'
      + '<path d="M150 86l30 11v22c0 20-13 34-30 40-17-6-30-20-30-40V97z" fill="var(--brand)" stroke="#fff" stroke-width="3"/>'
      + '<path d="M138 120l9 9 17-19" fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<rect x="58" y="176" width="184" height="12" rx="6" fill="#fff" fill-opacity=".14"/><rect x="58" y="176" width="128" height="12" rx="6" fill="var(--accent)"/>'
      + '<circle cx="62" cy="58" r="3" fill="#fff" fill-opacity=".5"/><circle cx="244" cy="74" r="2.5" fill="#fff" fill-opacity=".45"/><circle cx="226" cy="168" r="2" fill="#fff" fill-opacity=".4"/>');
    g.setAttribute('viewBox', '0 0 300 210'); g.setAttribute('class', 'art'); g.setAttribute('aria-hidden', 'true');
    return g;
  }
  function phoneHint() {
    const ph = svgFrom('<rect x="2" y="2" width="40" height="68" rx="8" fill="var(--card)" stroke="var(--line2)" stroke-width="2"/><rect x="16" y="6" width="12" height="3" rx="1.5" fill="var(--line2)"/>'
      + '<rect x="7" y="18" width="30" height="40" rx="4" fill="var(--sub)"/><circle cx="14" cy="27" r="4" fill="#1a73e8"/><circle cx="14" cy="27" r="1.6" fill="#fff"/>'
      + '<rect x="21" y="25" width="12" height="3" rx="1.5" fill="var(--line2)"/><text x="22" y="46" font-size="9.5" font-weight="700" text-anchor="middle" fill="var(--brand)" font-family="ui-monospace,monospace">123 456</text>'
      + '<rect x="9" y="50" width="26" height="2.5" rx="1.25" fill="var(--accent)"/>');
    ph.setAttribute('viewBox', '0 0 44 72'); ph.setAttribute('class', 'phone'); ph.setAttribute('aria-hidden', 'true');
    return h('div', { class: 'otphint' }, ph, h('div', {}, h('b', {}, t('Two-step verification')), h('small', { class: 'apps', dir: 'ltr' }, 'Google Authenticator · Microsoft Authenticator'),
      h('span', {}, t('Open the authenticator app on your phone and type the 6 digits shown under {0}. They change every 30 seconds.', (S.brand && S.brand.brandPRODUCT) || t('this system')))));
  }
  function authShell(form) {
    const b = S.brand || {};
    return h('div', { class: 'auth' }, h('aside', { class: 'auth-side' }, authArt(),
      h('span', { class: 'aibadge' }, '✦ ' + t('AI security')),
      h('h1', { class: 'pitch' }, t('Backup with next-generation AI security')),
      h('p', { class: 'prod' }, (b.brandPRODUCT || 'ITSguard Server Online') + (b.brandSLOGAN ? ' — ' + b.brandSLOGAN : '')),
      h('ul', {}, [t('AI watches every backup for ransomware and freezes the old versions at once'), t('AI explains a failed backup in plain words'), t('Encrypted on the customer\'s computer before it leaves'), t('Every sign-in protected by a second step')].map((x) => h('li', {}, x)))), form);
  }
  function loginPage() {
    S.me = null; brand(S.brand || {});
    const user = h('input', { type: 'text', autocomplete: 'username' }), pass = h('input', { type: 'password', autocomplete: 'current-password' }), otp = h('input', { type: 'text', inputmode: 'numeric', autocomplete: 'one-time-code', maxlength: '8', class: 'otp ltr', placeholder: '123456' });
    // AUTH-040 (owner: "wrong details — like every system in the world"): the error stays on the form in red, the password and
    // the code are emptied, the user name is kept and the password field takes the cursor; the button cannot be pressed twice
    const err = h('div', { class: 'loginerr', role: 'alert', hidden: true });
    const go = h('button', { class: 'btn pri', type: 'submit' }, t('Sign in'));
    main.replaceChildren(authShell(h('form', { class: 'login', onsubmit: async (e) => {
      e.preventDefault(); if (go.disabled) return;
      err.hidden = true; go.disabled = true;
      try {
        const r = await api('POST', 'login', { login: user.value, password: pass.value, otp: otp.value });
        S.session = r.session; sessionStorage.setItem('obAdmin', r.session); if (r.remember === '1') { try { localStorage.setItem('obAdminLocal', r.session); } catch (e2) { } } start();
      } catch (x) {
        err.textContent = x.message || t('Sign-in failed'); err.hidden = false;
        pass.value = ''; otp.value = ''; (user.value ? pass : user).focus();
      } finally { go.disabled = false; }
    } }, h('h2', {}, t('Administrator sign-in')), err, h('label', {}, t('User name')), user, h('label', {}, t('Password')), pass,
      h('label', {}, t('Verification code')), otp, phoneHint(), go)));
    user.focus();
  }
  // TECH-010: every administrator sets up two-step verification at the first sign-in — nothing else opens before
  async function enrollPage() {
    const r = await api('POST', 'totp/enable', {});
    const code = h('input', { type: 'text', inputmode: 'numeric', autocomplete: 'one-time-code' });
    let qr = null; try { const q = qrcode(0, 'M'); q.addData(r.uri); q.make(); qr = h('img', { class: 'qr', alt: '' }); qr.src = q.createDataURL(5, 8); } catch (e) { }
    main.replaceChildren(authShell(h('form', { class: 'login', onsubmit: wrap(async (e) => { e.preventDefault(); await api('POST', 'totp/confirm', { code: code.value }); toast(t('Two-step verification is on')); start(); }) },
      loginBrand(), h('h2', {}, t('Set up two-step verification')), h('p', { class: 'muted' }, t('Two-step verification is mandatory for every administrator. Scan the code with an authenticator app (Google Authenticator, Microsoft Authenticator, Authy) and type the code it shows.')),
      qr, h('p', { class: 'mono' }, r.secret), h('label', {}, t('Code from the app')), code, h('button', { class: 'btn pri', type: 'submit' }, t('Turn on')))));
    code.focus();
  }
  function logout() { if (S.session) { try { fetch('/api/admin/logout', { method: 'POST', headers: { 'X-Session': S.session } }).catch(() => { }); } catch (e) { } } S.session = null; S.me = null; sessionStorage.removeItem('obAdmin'); try { localStorage.removeItem('obAdminLocal'); } catch (e) { } loginPage(); }

  // ------------------------------------------------------------------ the rail and the router
  const NAV_SYSTEM = [
    ['', [['dash', '◧', 'Dashboard'], ['cust', '▦', 'Customers and sets', 'core'], ['allsets', '☰', 'All backup sets'], ['tasks', '✓', 'Tasks — 24 hours'], ['live', '⟳', 'Active backups']]],
    ['Service', [['tickets', '🎫', 'Service calls']]],
    ['AI', [['ai', '✦', 'Insights (AI)', 'ai']]],
    ['Operations', [['logs', '≣', 'Logs'], ['reports', '⎙', 'Reports'], ['restoretests', '⤺', 'Restore tests']]],
    ['Server', [['storage', '⛁', 'Storage on the server'], ['license', '⚿', 'Licence']]],
    ['Settings', [['defaults', '✚', 'Defaults for new customers'], ['policies', '⚙', 'Policies and templates'], ['notify', '✉', 'E-mails and alerts'], ['admins', '👥', 'Administrators'], ['tset', '🎫', 'Service call settings'], ['security', '🔒', 'Security and sign-in'], ['time', '◷', 'Clock and time zone'], ['integr', '⇄', 'Integrations'], ['client', '⬇', 'Client software'], ['contract', '✍', 'Contract and sign-up'], ['brand', '◐', 'Branding']]]];
  const NAV_VENDOR = [['', [['dash', '◧', 'Dashboard'], ['cust', '▦', 'My customers', 'core'], ['tasks', '✓', 'Tasks — 24 hours'], ['live', '⟳', 'Active backups'], ['tickets', '🎫', 'Service calls'], ['ai', '✦', 'Insights (AI)', 'ai'], ['brand', '◐', 'Branding']]]];
  const PAGES = {};
  function go(patch) {
    Object.assign(S, patch);
    try { sessionStorage.setItem('obNav', JSON.stringify({ page: S.page, login: S.login, ctab: S.ctab, set: S.set, stab: S.stab, tck: S.tck, tv: S.tv, adm: S.adm })); } catch (e) { }
    render(); window.scrollTo(0, 0);
  }
  async function render() {
    const nav = S.vendor ? NAV_VENDOR : NAV_SYSTEM;
    const known = nav.flatMap(([, items]) => items.map((i) => i[0])).concat(['customer']);
    if (!known.includes(S.page)) S.page = 'dash';
    const rail = h('nav', { class: 'rail', 'aria-label': t('Menu') });
    for (const [g, items] of nav) {
      if (g) rail.append(h('div', { class: 'grp' }, t(g)));
      for (const [k, ic, label, extra] of items) rail.append(h('button', { type: 'button', 'data-k': k, class: ((S.page === k || (k === 'cust' && S.page === 'customer')) ? 'on ' : '') + (extra || ''), onclick: () => go({ page: k, set: null, tck: null, adm: null }) }, h('span', { class: 'ic' }, ic), t(label)));
    }
    const content = h('main', { class: 'pg' }, h('p', { class: 'muted' }, t('Loading…')));
    main.replaceChildren(h('div', { class: 'app' }, rail, content));
    try { const p = await (PAGES[S.page] || PAGES.dash)(); if (p) content.replaceChildren(p); }
    catch (x) { content.replaceChildren(h('div', { class: 'note warn' }, '⚠', h('span', {}, x.message))); }
  }
  async function start() {
    try { S.me = await api('GET', 'me'); } catch (x) { return; }
    S.vendor = S.me.vendor || '';
    brand(S.me);
    if (S.me.enroll === '1') { enrollPage(); return; }
    checkUpdate(false); if (!S.updTimer) S.updTimer = setInterval(() => checkUpdate(false), 6 * 3600 * 1000);
    render();
  }

  // ------------------------------------------------------------------ data shared by the pages
  async function users() { const r = await api('GET', 'users'); S.users = list(r.users); return S.users; }
  const custName = (u) => (u && (u.alias || u.login)) || '';
  const statusPill = (s) => ({ ok: pill('ok', '✓ ' + t('Succeeded')), warn: pill('warn', '⚠ ' + t('Warnings')), bad: pill('bad', '✕ ' + t('Failed')), stopped: pill('mut', '■ ' + t('Stopped')) })[s] || pill('mut', s || '—');
  const KIND = { Backup: 'Backup', Restore: 'Restore', RestoreTest: 'Restore test' };
  const computersOf = (u) => [...new Set(list(u.sets).map((s) => s.computer).filter((c) => c && !c.startsWith('~')))];

  // ------------------------------------------------------------------ dashboard (DASH-010): each administrator chooses what it shows
  const WIDGETS = {
    kpi: ['Numbers — 24 hours', 'full', (d) => h('div', { class: 'grid g4' },
      tile('Tasks that ran', d.tasks || '0', t('{0} customers · {1} backup sets', d.customers, d.sets), () => go({ page: 'tasks', tv: 'all' })),
      tile('Succeeded', pct(d.ok, d.tasks), t('{0} tasks', d.ok || 0), () => go({ page: 'tasks', tv: 'ok' }), 'ok'),
      tile('Warnings', d.warn || '0', t('Locked files and the like'), () => go({ page: 'tasks', tv: 'warn' }), 'warn'),
      tile('Failed', d.bad || '0', t('Click for the list'), () => go({ page: 'tasks', tv: 'bad' }), 'bad'))],
    chart: ['14-day chart', 'wide', (d) => card(t('The last 14 days'), h('span', { class: 'muted small' }, t('Backups per day')), chart14(list(d.days)))],
    attention: ['Needs attention', 'wide', (d) => card(t('Needs attention'), btn(t('All tasks'), () => go({ page: 'tasks', tv: 'bad' }), 'sm'),
      table([t('Customer'), t('Computer / set'), t('Status'), t('When')], list(d.attention).map((r) => [h('b', {}, nameOf(r.login)), h('span', {}, r.computer || '—', h('span', { class: 'sub' }, r.setName || '')), statusPill(r.status), N(when(r.time))]),
        (i) => go({ page: 'customer', login: list(d.attention)[i].login, ctab: 'sets', set: null }), { empty: t('Nothing needs attention — every backup of the last 24 hours went well.') }))],
    live: ['Active backups', 'side', (d) => card(t('Running now'), btn(t('All'), () => go({ page: 'live' }), 'sm'), d.live.length ? h('div', { class: 'stack' }, d.live.map((x) => h('div', { class: 'prog', ondblclick: () => go({ page: 'customer', login: x.login, ctab: 'sets', set: x.set, stab: 'general' }) },
      h('span', { class: 'small' }, h('b', {}, nameOf(x.login)), ' · ', x.setName || '', ' · ', N(size(x.bytes))), bar(Number(x.percent) >= 0 ? Number(x.percent) : 50, 'ok')))) : h('p', { class: 'muted small' }, t('No backup is running now.')))],
    calls: ['My service calls', 'side', (d) => card(t('Service calls'), btn(t('All calls'), () => go({ page: 'tickets', tck: null }), 'sm'), h('div', { class: 'grid g3' },
      kv(t('Open'), d.callsOpen), kv(t('Late'), d.callsLate, Number(d.callsLate) > 0 ? 'bad' : ''), kv(t('Mine'), d.callsMine)))],
    license: ['Licence and storage', 'side', (d) => card(t('Licence and storage'), btn(t('Details'), () => go({ page: 'license' }), 'sm'), h('div', { class: 'stack' },
      meter(t('Customers'), Number(d.customers), Number(d.licenseCustomers)), meter(t('Computers'), Number(d.computers), Number(d.licenseComputers)),
      meter(t('Storage'), Number(d.usedBytes), Number(d.licenseStorageGB) * 1073741824, size)))],
    ai: ['The AI recommends', 'side', (d) => h('section', { class: 'ai-panel' }, h('div', { class: 'ch' }, h('h2', {}, h('span', { class: 'live' }), t('The AI is watching your backups')), pill('ai', '✦ AI')),
      h('div', { class: 'cb' }, btn(t('Open the insights'), () => go({ page: 'ai' }), 'sm')))]
  };
  const pct = (a, b) => Number(b) > 0 ? Math.round(Number(a) * 1000 / Number(b)) / 10 + '%' : '—';
  const tile = (label, value, sub, onclick, tone) => h('button', { class: 'card kpi tile', type: 'button', onclick }, h('span', { class: 'l' }, t(label)), h('span', { class: 'v', style: tone ? 'color:var(--' + tone + ')' : '' }, String(value)), h('span', { class: 'd muted' }, sub));
  const kv = (label, value, tone) => h('div', { class: 'kv' }, h('span', { class: 'muted small' }, label), h('b', { style: tone ? 'color:var(--' + tone + ')' : '' }, N(String(value || 0))));
  const nameOf = (login) => custName(list(S.users).find((u) => u.login === login)) || login;
  function chart14(days) {
    const W = 640, H = 170, pad = 34, max = Math.max(1, ...days.map((x) => Number(x.ok) + Number(x.warn) + Number(x.bad))), bw = (W - pad * 2) / Math.max(1, days.length);
    const y = (v) => H - 22 - (v / max) * (H - 40);
    const ns = 'http://www.w3.org/2000/svg', svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H); svg.setAttribute('class', 'chart'); svg.setAttribute('direction', 'ltr'); svg.setAttribute('role', 'img'); svg.setAttribute('aria-label', t('Backups per day'));
    const el = (n, a, txt) => { const e = document.createElementNS(ns, n); for (const [k, v] of Object.entries(a)) e.setAttribute(k, v); if (txt != null) e.textContent = txt; svg.append(e); };
    for (const v of [0, Math.round(max / 2), max]) { el('line', { x1: pad, x2: W - pad, y1: y(v), y2: y(v), stroke: 'var(--line)' }); el('text', { x: pad - 4, y: y(v) + 4, 'font-size': 11, fill: 'var(--muted)', 'text-anchor': 'end' }, v); }
    days.forEach((d, i) => {
      const x = pad + i * bw + 4, w = bw - 8, ok = Number(d.ok), wa = Number(d.warn), b = Number(d.bad);
      el('rect', { x, y: y(ok), width: w, height: y(0) - y(ok), rx: 3, fill: 'var(--brand)', opacity: i === days.length - 1 ? 1 : .6 });
      el('rect', { x, y: y(ok + wa), width: w, height: y(ok) - y(ok + wa), fill: 'var(--warn)' });
      el('rect', { x, y: y(ok + wa + b), width: w, height: y(ok + wa) - y(ok + wa + b), fill: 'var(--bad)' });
      if (i % 2 === 1 || i === days.length - 1) el('text', { x: x + w / 2, y: H - 6, 'font-size': 11, fill: 'var(--muted)', 'text-anchor': 'middle' }, d.day.slice(8, 10) + '/' + d.day.slice(5, 7));
    });
    return h('div', {}, svg, h('div', { class: 'legend' }, h('span', {}, h('i', { class: 'dot', style: 'background:var(--brand)' }), t('Succeeded')), h('span', {}, h('i', { class: 'dot warn' }), t('Warnings')), h('span', {}, h('i', { class: 'dot bad' }), t('Failed'))));
  }
  PAGES.dash = async () => {
    const [d, lv] = await Promise.all([api('GET', 'dashboard'), api('GET', 'live'), users()]); d.live = list(lv.live);
    let chosen; try { chosen = JSON.parse(localStorage.getItem('obDash') || 'null'); } catch (e) { }
    if (!Array.isArray(chosen)) chosen = ['kpi', 'chart', 'live', 'attention', 'calls', 'license', 'ai'];
    chosen = chosen.filter((k) => WIDGETS[k]);
    const save = (a) => { try { localStorage.setItem('obDash', JSON.stringify(a)); } catch (e) { } render(); };
    const editor = !S.editDash ? null : card(t('Customize — what the dashboard shows'), btn(t('Done'), () => { S.editDash = false; render(); }, 'pri sm'),
      h('div', { class: 'stack' }, Object.entries(WIDGETS).map(([k, [label]]) => { const on = chosen.includes(k); const i = chosen.indexOf(k);
        return h('div', { class: 'inline' }, togRow(on, t(label)), on ? btn('▲', () => { const a = chosen.slice(); if (i > 0) [a[i - 1], a[i]] = [a[i], a[i - 1]]; save(a); }, 'sm') : null, on ? btn('▼', () => { const a = chosen.slice(); if (i < a.length - 1) [a[i + 1], a[i]] = [a[i], a[i + 1]]; save(a); }, 'sm') : null); })));
    if (editor) editor.querySelectorAll('.tog input').forEach((inpEl, idx) => inpEl.addEventListener('change', () => { const k = Object.keys(WIDGETS)[idx]; save(inpEl.checked ? chosen.concat(k) : chosen.filter((x) => x !== k)); }));
    return h('div', { class: 'stack' }, head(t('Dashboard'), t('Hello {0}. Choose what interests you in "Customize".', S.me.admin), [btn('✎ ' + t('Customize'), () => { S.editDash = !S.editDash; render(); })]), editor,
      h('div', { class: 'board' }, chosen.map((k) => h('div', { class: 'w-' + WIDGETS[k][1] }, WIDGETS[k][2](d)))));
  };

  // ------------------------------------------------------------------ customers and sets (the heart of the system)
  PAGES.cust = async () => {
    const us = await users();
    const q = inp('', { class: 'search', placeholder: t('Search a customer, user name or e-mail…') });
    const chosen = new Set();
    const body = h('div', {});
    const bulk = h('div', { class: 'tools hide' });
    const draw = () => {
      const f = q.value.trim().toLowerCase();
      const rows = us.filter((u) => !f || [u.login, u.alias, ...list(u.contacts).map((c) => c.email)].join(' ').toLowerCase().includes(f));
      body.replaceChildren(table(['', t('Customer'), t('Sets'), t('Computers'), t('Used / quota'), t('Last backup'), t('Status')], rows.map((u) => {
        const cb = h('input', { type: 'checkbox', checked: chosen.has(u.login), 'aria-label': t('Choose'), onclick: (e) => { e.stopPropagation(); if (e.target.checked) chosen.add(u.login); else chosen.delete(u.login); drawBulk(); } });
        const used = Number(u.quotaType === 'UNCOMPRESSED' ? u.dataOrig : u.dataSize) + Number(u.quotaType === 'UNCOMPRESSED' ? u.retainOrig : u.retainSize), quota = Number(u.quota);
        return [cb, h('span', {}, h('b', {}, custName(u)), h('span', { class: 'sub ltr' }, u.login)), N(list(u.sets).length), N(computersOf(u).length),
          h('div', { class: 'prog' }, h('span', { class: 'small' }, N(size(used) + (quota > 0 ? ' / ' + size(quota) : ''))), quota > 0 ? bar(Math.round(used * 100 / quota)) : null),
          N(when(u.lastBackup)), h('div', { class: 'chips' }, u.locked === '1' ? pill('bad', t('Locked')) : null, u.frozen === '1' ? pill('bad', t('Frozen — ransomware?')) : null, u.totp === '1' ? pill('ok', '2FA') : null, u.signup === 'CLIENT' ? pill('info', t('Signed up')) : null)];
      }), (i) => go({ page: 'customer', login: rows[i].login, ctab: 'sets', set: null }), { empty: t('No customers yet. New customers sign up from the client software.') }));
    };
    const drawBulk = () => {
      bulk.classList.toggle('hide', chosen.size === 0);
      bulk.replaceChildren(h('b', {}, t('{0} chosen', chosen.size)), btn('▶ ' + t('Back up now'), () => bulkAct('run')), btn(t('Apply a template'), bulkTemplate), btn(t('Quota'), bulkQuota), btn(t('What customers may change'), bulkRights), btn(t('Clear'), () => { chosen.clear(); draw(); drawBulk(); }, 'link'));
    };
    const bulkAct = async (action, extra) => { const r = await api('POST', 'bulk', Object.assign({ action, logins: [...chosen].map((login) => ({ login })) }, extra || {})); toast(t('Done for {0} of {1} customers', r.done, chosen.size)); return r; };
    const bulkTemplate = async () => { const tp = list((await api('GET', 'templates')).templates); if (!tp.length) { toast(t('There are no templates yet — create one in "Policies and templates".'), true); return; } const s = sel(tp.map((x) => [x.name, x.name])); dialog(t('Apply a template'), h('div', { class: 'form' }, fr(t('Template'), t('Applied to every set of its type'), s)), async () => { await bulkAct('template', { template: s.value }); }); };
    const bulkQuota = () => { const g = num(100, { min: 1 }); const tp = sel([['COMPRESSED', t('Compressed size')], ['UNCOMPRESSED', t('Original size')]], 'COMPRESSED'); dialog(t('Quota'), h('div', { class: 'form' }, fr(t('Quota (GB)'), null, g), fr(t('Count by'), null, tp)), async () => { await bulkAct('quota', { quotaGB: g.value, quotaType: tp.value }); render(); }); };
    const bulkRights = () => { const rows = RIGHTS.map(([k, label, def]) => togRow(def, t(label))); dialog(t('What customers may change'), h('div', { class: 'stack' }, rows), async () => { const b = {}; RIGHTS.forEach(([k], i) => { b[k] = rows[i].input.checked ? 1 : 0; }); await bulkAct('rights', b); }); };
    q.addEventListener('input', draw); draw();
    return h('div', { class: 'stack' }, head(S.vendor ? t('My customers') : t('Customers and sets'), t('{0} customers. Click a customer for its backup sets and settings. New customers sign up from the client software.', us.length)),
      h('section', { class: 'card' }, h('div', { class: 'tools' }, q), bulk, body));
  };
  const RIGHTS = [['can_add_sets', 'Add backup sets', true], ['can_edit_sources', 'Change what is backed up', true], ['can_edit_schedule', 'Change the schedule', true], ['can_edit_retention', 'Change how long versions are kept', false], ['can_edit_destination', 'Change the destination', false], ['can_edit_options', 'Change other options', false]];

  PAGES.allsets = async () => {
    const us = await users();
    const rows = us.flatMap((u) => list(u.sets).map((s) => ({ u, s })));
    return h('div', { class: 'stack' }, head(t('All backup sets'), t('{0} sets of all customers.', rows.length)),
      h('section', { class: 'card' }, table([t('Customer'), t('Set'), t('Type'), t('Computer'), t('Data'), t('Last backup'), t('Restore test')], rows.map(({ u, s }) => [h('b', {}, custName(u)), s.name, TYPES[s.type] ? t(TYPES[s.type]) : s.type, s.computer || '—', N(size(s.dataSize)), lastCell(s), s.restoreTest ? (s.restoreTest.startsWith('OK') ? pill('ok', t('Passed')) : s.restoreTest.startsWith('NOT_CHECKED') ? pill('mut', t('Nothing to compare')) : pill('bad', t('Failed'))) : pill('mut', t('Not yet'))]),
        (i) => go({ page: 'customer', login: rows[i].u.login, ctab: 'sets', set: rows[i].s.id, stab: 'general' }))));
  };
  const TYPES = { FILE: 'Files', MSSQL: 'SQL Server', SYSTEMSTATE: 'System State', BAREMETAL: 'Whole computer', M365: 'Microsoft 365', GWS: 'Google Workspace', MYSQL: 'MySQL / MariaDB', POSTGRESQL: 'PostgreSQL', ORACLE: 'Oracle', DOMINO: 'HCL Domino', HYPERV: 'Hyper-V', VMWARE: 'VMware' };

  // ------------------------------------------------------------------ one customer
  PAGES.customer = async () => {
    const us = await users();
    const u = us.find((x) => x.login === S.login);
    if (!u) { S.page = 'cust'; return PAGES.cust(); }
    const tabs = [['sets', 'Backup sets and settings'], ['overview', 'Overview'], ['pcs', 'Computers'], ['contacts', 'Contacts and e-mails'], ['quota', 'Quota and pricing'], ['sec', 'Security'], ['calls', 'Service calls'], ['reports', 'Reports'], ['logs', 'Log']];
    const T = { sets: custSets, overview: custOverview, pcs: custPcs, contacts: custContacts, quota: custQuota, sec: custSec, calls: custCalls, reports: custReports, logs: custLogs };
    const body = S.ctab === 'sets' && S.set ? await setEditor(u) : await (T[S.ctab] || custSets)(u);
    return h('div', {},
      head(custName(u), null, [btn('▶ ' + t('Back up now — everything'), async () => { await api('POST', 'bulk', { action: 'run', logins: [{ login: u.login }] }); toast(t('The backup was started on the customer\'s computers')); }),
        btn('⎙ ' + t('Data-protection report'), () => complianceReport(u.login)), btn('🗑 ' + t('Delete customer'), async () => { if (!await confirmBox(t('Delete {0}? It moves to the recycle bin for 14 days.', custName(u)))) return; const r = await api('POST', 'users/' + enc(u.login) + '/delete'); toast(r.pending === '1' ? t('Another administrator must approve the deletion.') : t('Moved to the recycle bin')); if (r.pending !== '1') go({ page: 'cust' }); }, 'danger')],
        [h('button', { type: 'button', onclick: () => go({ page: 'cust' }) }, t('Customers and sets')), '‹', custName(u)]),
      h('div', { class: 'tabs', role: 'tablist' }, tabs.map(([k, label]) => h('button', { type: 'button', 'data-k': k, class: S.ctab === k ? 'on' : '', onclick: () => go({ ctab: k, set: null }) }, t(label)))), body);
  };
  async function complianceReport(login) {
    const r = await fetch('/api/admin/users/' + enc(login) + '/compliance?lang=' + enc(I18N.lang), { headers: { 'X-Session': S.session } });
    if (!r.ok) throw new Error(t('Error {0}', r.status));
    const html = await r.text(); const w = window.open('', '_blank');
    if (!w) { const a = h('a', { href: URL.createObjectURL(new Blob([html], { type: 'text/html' })), download: 'report-' + login + '.html' }); document.body.append(a); a.click(); a.remove(); return; }
    w.document.open(); w.document.write(html); w.document.close();
  }
  function custSets(u) {
    const sets = list(u.sets).filter((s) => !(s.computer || '').startsWith('~'));
    const head = h('span', { class: 'muted small' }, t('A new set is added on the customer\'s computer (client software).'));
    if (!sets.length) return card(t('Backup sets'), head, h('p', { class: 'muted' }, t('No backup sets yet. The customer adds them in the client software after installing it.')));
    // like Ahsay OBM: each computer (server) with its own sets, each set with its own folders
    const pcs = [...new Set(sets.map((s) => s.computer || ''))].sort((a, b) => a.localeCompare(b));
    const what = (s) => { const src = (s.sources || '').split('\n').filter(Boolean); return src.length ? h('span', { class: 'ltr small' }, ltr(src[0]), (src.length > 1 ? ' +' + (src.length - 1) : '') + (Number(s.skipped) > 0 ? ' · ✕' + s.skipped : '')) : h('span', { class: 'muted small' }, TYPES[s.type] ? t(TYPES[s.type]) : s.type); };
    return card(t('Backup sets'), head, h('div', { class: 'stack' }, pcs.map((pc) => {
      const mine = sets.filter((s) => (s.computer || '') === pc);
      return h('div', { class: 'pcgroup' }, h('h4', { class: 'pch' }, '🖥 ', h('span', { class: 'ltr' }, pc || t('No computer yet')), h('span', { class: 'muted small' }, ' · ' + t('{0} sets', mine.length))),
        table([t('Name'), t('What to back up'), t('Data'), t('Last backup'), t('Restore test'), ''], mine.map((s) => [h('b', {}, s.name), what(s), N(size(s.dataSize)), lastCell(s),
          s.restoreTest ? (s.restoreTest.startsWith('OK') ? pill('ok', t('Passed')) : s.restoreTest.startsWith('NOT_CHECKED') ? pill('mut', t('Nothing to compare')) : pill('bad', t('Failed'))) : pill('mut', t('Not yet')),
          h('div', { class: 'inline' }, h('button', { class: 'btn sm', type: 'button', title: t('Back up now'), onclick: wrap(async (e) => { e.stopPropagation(); await api('POST', 'users/' + enc(u.login) + '/sets/' + s.id + '/run'); toast(t('The backup was started on the customer\'s computer')); }) }, '▶'),
            h('button', { class: 'btn sm', type: 'button', title: t('Stop'), onclick: wrap(async (e) => { e.stopPropagation(); await api('POST', 'users/' + enc(u.login) + '/sets/' + s.id + '/stop'); toast(t('A stop request was sent')); }) }, '■'))]),
          (i) => go({ set: mine[i].id, stab: 'general' })));
    })));
  }
  function custOverview(u) {
    const used = Number(u.dataSize) + Number(u.retainSize), quota = Number(u.quota);
    return h('div', { class: 'stack' }, h('div', { class: 'grid g4' }, kpiBox(t('Computers'), computersOf(u).length), kpiBox(t('Backup sets'), list(u.sets).length),
      kpiBox(t('Used'), size(used), quota > 0 ? t('of {0} · counted by {1}', size(quota), u.quotaType === 'UNCOMPRESSED' ? t('original size') : t('compressed size')) : ''), kpiBox(t('Last backup'), when(u.lastBackup))),
      card(t('Details'), null, h('div', { class: 'form' }, fr(t('User name'), null, ltr(u.login)), fr(t('Phone'), null, u.phone || '—'), fr(t('Signed up'), null, u.signup === 'CLIENT' ? t('From the client software') : t('By the IT company')), fr(t('Contract'), null, u.contractVersion ? t('Version {0} accepted', u.contractVersion) : '—'))));
  }
  const kpiBox = (label, value, sub) => h('div', { class: 'card kpi' }, h('span', { class: 'l' }, label), h('span', { class: 'v' }, N(String(value))), sub ? h('span', { class: 'd muted' }, sub) : null);
  // COMP-010: the customer's computers — last connection, version; disconnect, or move to another customer with the backups
  async function custPcs(u) {
    const r = await api('GET', 'users/' + enc(u.login) + '/computers');
    const pcs = list(r.computers);
    const others = list(S.users).filter((x) => x.login !== u.login);
    const act = (pc) => {
      const move = sel([['', t('Move to another customer…')], ...others.map((x) => [x.login, custName(x)])], '');
      move.addEventListener('change', wrap(async () => {
        const target = others.find((x) => x.login === move.value); if (!target) return;
        if (!await confirmBox(t('Move {0} with its backup sets and backups to {1}? Then install the client software on it again with the user name of {1}.', pc.name, custName(target)))) { move.value = ''; return; }
        const m = await api('POST', 'users/' + enc(u.login) + '/computers/move', { computer: pc.name, target: target.login }); toast(t('Moved: {0} backup sets', m.sets)); await users(true); render();
      }));
      return h('div', { class: 'inline' }, pc.connected === '1' ? btn(t('Disconnect'), async () => {
        if (!await confirmBox(t('Disconnect {0}? It stops backing up; its backups are kept and the licence place is freed.', pc.name))) return;
        await api('POST', 'users/' + enc(u.login) + '/computers/disconnect', { computer: pc.name }); toast(t('Disconnected')); render(); }, 'sm') : null,
        Number(pc.sets) > 0 && others.length ? move : null);
    };
    return card(t('Computers ({0})', pcs.length), h('span', { class: 'muted small' }, t('A computer is added when the client software is installed and signed in.')),
      table([t('Computer'), t('Backup sets'), t('Last backup'), t('Last connection'), t('Version'), t('Status'), ''], pcs.map((pc) => [h('b', {}, ltr(pc.name)), pc.setNames || '—', N(when(pc.lastBackup)),
        h('span', {}, N(when(pc.lastSeen)), pc.lastIp ? h('span', { class: 'sub ltr' }, pc.lastIp) : null), h('span', {}, ltr(pc.version || '—'), pc.os ? h('span', { class: 'sub ltr small' }, pc.os) : null),
        pc.connected === '1' ? pill('ok', t('Connected')) : pill('mut', t('Disconnected')), act(pc)]), null, { empty: t('No computers yet.') }));
  }
  function custContacts(u) {
    const rows = h('div', { class: 'stack' });
    const add = (c) => { const n = inp(c.name || '', { placeholder: t('Name') }), e = inp(c.email || '', { placeholder: 'name@example.com', class: 'ltr' }); const r = h('div', { class: 'frow' }, n, e, h('button', { class: 'btn sm danger', type: 'button', 'aria-label': t('Remove'), onclick: () => r.remove() }, '✕')); r.get = () => ({ name: n.value, email: e.value }); rows.append(r); };
    list(u.contacts).forEach(add);
    const notify = sel([['ALL', t('Every backup')], ['FAILURE', t('Only failures and warnings')], ['NONE', t('None')]], u.notify || 'ALL');
    return h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('E-mails of the customer'), t('Reports and alerts go to all of them. One customer can have several e-mails; there is no e-mail per set.'), rows, h('div', {}, btn('+ ' + t('Add an e-mail'), () => add({}), 'sm'))),
      fr(t('Reports by e-mail'), null, notify))),
      savebar(async () => { await api('POST', 'users/' + enc(u.login) + '/contacts', { notify: notify.value, contacts: Array.from(rows.children).map((r) => r.get()) }); }, () => go({ ctab: 'sets' })));
  }
  function custQuota(u) {
    const g = num(Math.round(Number(u.quota) / 1073741824), { min: 1 });
    const basis = h('div', { class: 'opt' }, [['COMPRESSED', t('Compressed size'), t('What is really kept on your server (usually much smaller)')], ['UNCOMPRESSED', t('Original size'), t('The size of the files on the customer\'s computer')]].map(([v, b, sp]) => h('label', {}, h('input', { type: 'radio', name: 'basis', value: v, checked: (u.quotaType || 'COMPRESSED') === v }), h('div', {}, h('b', {}, b), h('span', {}, sp)))));
    return h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('Quota (GB)'), t('The customer\'s backups cannot grow beyond it'), g),
      fr(t('Count the quota and the price by'), t('You choose for each customer'), basis),
      fr(t('Used now'), null, meter(t('Used'), Number(u.dataSize) + Number(u.retainSize), Number(u.quota), size)))),
      savebar(async () => { await api('POST', 'users/' + enc(u.login) + '/quota', { quotaGB: g.value, quotaType: basis.querySelector('input:checked').value }); }, () => go({ ctab: 'sets' })));
  }
  function custSec(u) {
    const ips = h('textarea', { class: 'ltr' }, u.allowedIps || '');
    const att = num(u.lockAttempts || '', { min: 1, max: 10, placeholder: t('Server default') }), mins = num(u.lockMinutes || '', { min: 5, placeholder: t('Server default') });
    const totp = togRow(u.requireTotp === '1', t('Required for this customer'));
    const rights = RIGHTS.map(([k, label, def]) => { const v = list(u.rights)[0] || {}; return togRow(v[k] != null ? v[k] === '1' : def, t(label)); });
    return h('div', { class: 'stack' }, h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('Two-step verification'), t('In the client software, the web restore and the account sign-in'), totp),
      fr(t('Allowed addresses'), t('Backup and restore only from these addresses. Empty = from anywhere'), ips),
      fr(t('Lock after wrong passwords'), t('The lock cannot be switched off: at most 10 attempts, at least 5 minutes.'), h('div', { class: 'inline' }, att, h('span', {}, t('attempts, for')), mins, h('span', {}, t('minutes')))),
      fr(t('What the customer may change'), t('In the client software. The rest is locked and managed here.'), h('div', { class: 'stack' }, rights)))),
      savebar(async () => {
        await api('POST', 'users/' + enc(u.login) + '/security', { allowedIps: ips.value, lockAttempts: att.value, lockMinutes: mins.value, requireTotp: totp.input.checked ? 1 : 0 });
        const b = {}; RIGHTS.forEach(([k], i) => { b[k] = rights[i].input.checked ? 1 : 0; }); await api('POST', 'users/' + enc(u.login) + '/details', b);
      }, () => go({ ctab: 'sets' }))),
      card(t('Actions'), null, h('div', { class: 'inline' },
        u.locked === '1' ? btn('🔓 ' + t('Unlock'), async () => { await api('POST', 'users/' + enc(u.login) + '/unlock'); toast(t('Unlocked')); render(); }) : null,
        u.totp === '1' ? btn(t('Reset two-step verification'), async () => { if (!await confirmBox(t('Reset two-step verification of {0}?', custName(u)))) return; await api('POST', 'users/' + enc(u.login) + '/resettotp'); toast(t('Verification reset')); render(); }) : null,
        u.frozen === '1' ? btn(t('Release the freeze'), async () => { if (!await confirmBox(t('Release the retention freeze of {0}? Check the computer first.', custName(u)))) return; await api('POST', 'users/' + enc(u.login) + '/unfreeze'); toast(t('Released')); render(); }) : h('span', { class: 'muted small' }, t('No lock, no freeze.')))));
  }
  async function custCalls(u) {
    const r = await api('GET', 'tickets?scope=all&login=' + enc(u.login));
    return card(t('Service calls of {0}', custName(u)), btn('+ ' + t('Call'), () => go({ page: 'tickets', tck: 'new', newLogin: u.login }), 'pri sm'), ticketTable(list(r.tickets)));
  }
  function custReports(u) {
    return card(t('Reports'), null, h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('For the customer and for cyber insurance: GDPR, Israeli privacy law Amendment 13, ISO 27001.')), h('div', {}, btn('⎙ ' + t('Data-protection report and restore certificate'), () => complianceReport(u.login), 'pri'))));
  }
  async function custLogs(u) { return logsView({ login: u.login }); }

  // ------------------------------------------------------------------ the set editor (SET-010): every setting, like Ahsay
  async function setEditor(u) {
    const path = 'users/' + enc(u.login) + '/sets/' + S.set;
    const d = await api('GET', path);
    const doc = new DOMParser().parseFromString(d.set, 'application/xml'); const E = doc.documentElement;
    const A = (n, def) => E.hasAttribute(n) ? E.getAttribute(n) : def;
    const setA = (n, v) => E.setAttribute(n, String(v));
    const kids = (n) => Array.from(E.children).filter((c) => c.tagName === n);
    const drop = (n) => kids(n).forEach((c) => c.remove());
    const addEl = (n, attrs, text) => { const c = doc.createElement(n); for (const [k, v] of Object.entries(attrs || {})) c.setAttribute(k, String(v)); if (text != null) c.textContent = text; E.append(c); return c; };
    const type = A('TYPE', 'FILE'), restic = A('ENGINE', '') === 'RESTIC';
    const collectors = [];   // each tab's "write my fields back into the XML"
    const done = () => go({ set: null });

    const tabs = [['general', 'General'], ['src', 'What to back up'], ['sched', 'Schedule'], ['method', 'Backup method'], ['dest', 'Destination'], ['ret', 'Versions kept'], ['filter', 'Filters'], ['enc', 'Encryption and compression'], ['perf', 'Resources'], ['cmd', 'Commands'], ['rep', 'Reports'], ['maint', 'Maintenance']];
    const panes = {};
    const body = h('div', {});
    const tabBar = h('div', { class: 'tabs', role: 'tablist' });
    const show = (k) => { S.stab = k; tabBar.querySelectorAll('button').forEach((b) => b.classList.toggle('on', b.dataset.k === k)); body.replaceChildren(panes[k] || (panes[k] = build[k]())); };
    tabs.forEach(([k, label]) => { const b = h('button', { type: 'button', onclick: () => show(k) }, t(label)); b.dataset.k = k; tabBar.append(b); });

    // ---- general: name and computer — like Ahsay OBM, a set belongs to one computer and has its own folders
    const build = {};
    build.general = () => {
      const name = inp(A('NAME', ''));
      const vss = togRow(A('ENABLED_SHADOW_COPY', 'Y') === 'Y', t('Volume Shadow Copy (VSS) — open files are read from a snapshot'));
      const perm = togRow(A('BSET_UPLOAD_PERMISSION', 'Y') === 'Y', t('Keep NTFS permissions and ownership'));
      collectors.push(() => {
        setA('NAME', name.value.trim());
        setA('ENABLED_SHADOW_COPY', vss.input.checked ? 'Y' : 'N'); setA('BSET_UPLOAD_PERMISSION', perm.input.checked ? 'Y' : 'N');
      });
      const pc = d.computer || '';
      const known = [...new Set(list(list(S.users).find((x) => x.login === u.login).sets).map((s) => s.computer).filter((c) => c && !c.startsWith('~')))];
      const others = known.filter((c) => c.toLowerCase() !== pc.toLowerCase()).map((c) => [c, c]);
      const copyTo = sel([['', t('Copy this set to another computer…')], ...others], '');
      copyTo.addEventListener('change', wrap(async () => { const c = copyTo.value; if (!c) return; const r = await api('POST', path + '/addcomputer', { computer: c }); toast(t('A set of its own was made for {0} — choose its folders.', c)); await users(true); go({ set: r.id, stab: 'src' }); }));
      const move = sel([['', t('Move to another computer…')], ...others], '');
      move.addEventListener('change', wrap(async () => { const c = move.value; if (!c) return; if (!await confirmBox(t('Move the set to {0}? Its backups stay with the set.', c))) { move.value = ''; return; } await api('POST', path + '/move', { computer: c }); toast(t('Moved to {0}', c)); await users(true); render(); }));
      const related = list(d.related).filter((r) => r.detached !== '1');
      return h('div', { class: 'form' },
        fr(t('Set name'), null, name),
        fr(t('Computer'), t('Each set belongs to one computer and has its own folders and settings, like Ahsay OBM.'), h('div', { class: 'inline' }, h('b', { class: 'ltr' }, pc || '—'), others.length ? move : null)),
        fr(t('Other computers'), t('The same backup on another server: a set of its own for that computer, starting from these settings — then choose its folders.'),
          related.length ? h('div', { class: 'chips' }, related.map((r) => h('button', { class: 'chip on', type: 'button', onclick: () => go({ set: r.id, stab: 'general' }) }, r.name, ' · ', h('span', { class: 'ltr' }, r.computer)))) : null,
          others.length ? h('div', { class: 'inline' }, copyTo) : h('span', { class: 'muted small' }, t('A computer is added when the client software is installed and signed in.'))),
        fr(t('Type'), null, h('span', {}, TYPES[type] ? t(TYPES[type]) : type, restic ? ' · restic' : '')),
        type === 'FILE' ? fr(t('Open files'), null, vss) : null, type === 'FILE' ? fr(t('Permissions'), null, perm) : null);
    };
    // ---- what to back up (SRC-030): the computer's folder tree — ✓ backs up a folder with everything in it, ✕ skips a folder inside
    build.src = () => type === 'MSSQL' ? sqlSource() : ['MYSQL', 'POSTGRESQL', 'ORACLE', 'VMWARE', 'HYPERV', 'DOMINO'].includes(type) ? listSource() : fileSource();
    // SQL Server (SQL-040): the server, its sign-in (usually sa), the databases — each backed up by SQL Server's own BACKUP
    const SQLP = 'Microsoft SQL Server';
    const sqlSource = () => {
      const srcs = kids('SEL-SOURCE').map((c) => c.textContent).filter(Boolean).map((x) => x.split('\\'));
      const inst = inp((srcs[0] && srcs[0][1]) || '', { class: 'ltr', placeholder: '.\\SQLEXPRESS' });
      const user = inp(A('ADMIN_USERNAME', ''), { class: 'ltr', placeholder: 'sa', autocomplete: 'off' });
      const dbs = h('textarea', { class: 'ltr', rows: 4, placeholder: 'ERP\nPayroll' }, srcs.filter((x) => x.length >= 3).map((x) => x.slice(2).join('\\')).join('\n'));
      const work = inp(A('WORKING_DIR', ''), { class: 'ltr', placeholder: 'E:\\SQLTemp' });
      collectors.push(() => {
        const i = inst.value.trim() || ((srcs[0] && srcs[0][1]) || '.'); const list = dbs.value.split(/[\n,]+/).map((x) => x.trim()).filter(Boolean);
        drop('SEL-SOURCE'); (list.length ? list.map((d) => SQLP + '\\' + i + '\\' + d) : [SQLP + '\\' + i]).forEach((v) => addEl('SEL-SOURCE', {}, v));
        setA('ADMIN_USERNAME', user.value.trim()); setA('WORKING_DIR', work.value.trim());
      });
      return h('div', { class: 'form' },
        fr(t('SQL Server'), t('The server and instance, as in SQL Server Management Studio — e.g. . or .\\SQLEXPRESS or SRV01\\ERP'), inst),
        fr(t('Sign-in'), t('A SQL login with the right to back up — usually sa. Empty = Windows authentication (the backup service\'s account).'), user,
          h('div', { class: 'note lock' }, '🔒', h('span', {}, t('The password is typed on the customer\'s computer only and kept there encrypted — it never comes to this server.')))),
        fr(t('Databases'), t('One per line. Empty = every database of the server (tempdb is never backed up).'), dbs),
        fr(t('Temporary folder'), t('SQL Server writes each backup file here before it is sent — free space at least the size of the largest database. Empty = the backup software\'s folder.'), work),
        h('div', { class: 'note' }, 'ⓘ', h('span', {}, t('Full, differential and transaction-log backups: the "Backup method" tab.'))));
    };
    // databases / virtual machines: one per line
    const listSource = () => {
      const area = h('textarea', { class: 'ltr', rows: 5 }, kids('SEL-SOURCE').map((c) => c.textContent).filter(Boolean).join('\n'));
      const host = inp(A('DB_HOST', ''), { class: 'ltr', placeholder: '127.0.0.1' }), user = inp(A('ADMIN_USERNAME', ''), { class: 'ltr' });
      collectors.push(() => { drop('SEL-SOURCE'); area.value.split(/\n+/).map((x) => x.trim()).filter(Boolean).forEach((v) => addEl('SEL-SOURCE', {}, v)); setA('DB_HOST', host.value.trim()); setA('ADMIN_USERNAME', user.value.trim()); });
      return h('div', { class: 'form' }, type === 'HYPERV' || type === 'DOMINO' ? null : fr(t('Server'), null, host), type === 'HYPERV' || type === 'DOMINO' ? null : fr(t('Sign-in'), t('The password is typed on the customer\'s computer only.'), user),
        fr(type === 'VMWARE' || type === 'HYPERV' ? t('Virtual machines') : t('Databases'), t('One per line. Empty = all.'), area));
    };
    const fileSource = () => {
      const inc = kids('SEL-SOURCE').map((c) => c.textContent).filter(Boolean), exc = kids('DE-SOURCE').map((c) => c.textContent).filter(Boolean);
      const common = togRow(kids('FILTER').some((f) => f.getAttribute('NAME') === 'COMMON'), t('Recycle bin, temporary files, page file, Thumbs.db, ~$ Office files'));
      collectors.push(() => {
        drop('SEL-SOURCE'); drop('DE-SOURCE'); inc.forEach((v) => addEl('SEL-SOURCE', {}, v)); exc.forEach((v) => addEl('DE-SOURCE', {}, v));
        kids('FILTER').filter((f) => f.getAttribute('NAME') === 'COMMON').forEach((f) => f.remove());
        if (common.input.checked) [[COMMON_FILES, 'N', 'Y'], [COMMON_DIRS, 'Y', 'N']].forEach(([pats, dir, file]) => { const f = addEl('FILTER', { TYPE: 'WILDCARD', TOP_DIR: '', INCLUDE: 'N', ONLY: 'Y', APPLY_DIR: dir, APPLY_FILE: file, NAME: 'COMMON', ID: Date.now() }); pats.forEach((x) => { const pe = doc.createElement('PATTERN'); pe.textContent = x; f.append(pe); }); });
      });
      return h('div', { class: 'form' },
        fr(t('What to back up'), t('✓ backs up the folder and everything in it. Inside a chosen folder, ✕ skips a folder.'), folderPicker(u, d.computer || '', inc, exc)),
        fr(t('Skip system and temporary files'), null, common));
    };
    // ---- schedule: days, round hour, maximum duration, missed runs
    build.sched = () => {
      // SCHED-020: one or more times, each with its days (like Ahsay's several schedules)
      const all = Array.from(E.children).filter((c) => c.tagName === 'DAILY_SCHEDULE' || c.tagName === 'WEEKLY_SCHEDULE');
      const first = all[0];
      const g = (n, def) => first && first.hasAttribute(n) ? first.getAttribute(n) : def;
      const dayKeys = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'], dayNames = [t('Sun'), t('Mon'), t('Tue'), t('Wed'), t('Thu'), t('Fri'), t('Sat')];
      const rows = h('div', { class: 'stack' });
      const addRow = (sch) => {
        const daily = !sch || sch.tagName === 'DAILY_SCHEDULE';
        const boxes = dayKeys.map((k, i) => { const c = h('input', { type: 'checkbox', checked: daily || sch.getAttribute(k) === 'Y' }); return h('label', { class: 'inline' }, c, dayNames[i]); });
        const hh = sel(Array.from({ length: 24 }, (_, i) => [String(i), String(i).padStart(2, '0')]), sch ? sch.getAttribute('HOUR') : '12');
        const mm = sel(Array.from({ length: 12 }, (_, i) => [String(i * 5), String(i * 5).padStart(2, '0')]), String(Math.round(Number(sch ? sch.getAttribute('MINUTE') : 0) / 5) * 5));
        const row = h('div', { class: 'sched' }, h('div', { class: 'inline' }, boxes), h('div', { class: 'inline ltr' }, hh, h('b', {}, ':'), mm),
          h('button', { class: 'btn sm danger', type: 'button', 'aria-label': t('Remove'), onclick: () => { if (rows.children.length > 1) row.remove(); else toast(t('A set needs at least one time — change it instead.'), true); } }, '✕'));
        row.get = () => ({ on: boxes.map((l) => l.querySelector('input').checked), hour: hh.value, minute: mm.value });
        rows.append(row);
      };
      (all.length ? all : [null]).forEach(addRow);
      const dur = sel([['-1', t('No limit')], ...[1, 2, 3, 4, 6, 8, 10, 12, 24].map((x) => [String(x), t('{0} hours', x)])], g('DURATION', '-1'));
      const missed = togRow(A('RUN_MISSED', 'Y') === 'Y', t('Run a missed backup when the computer is on again'));
      const missedNet = togRow(A('RUN_MISSED_NET', 'Y') === 'Y', t('When the internet is back: start a backup that was missed or cut off, right away'));
      const delay = num(A('MISSED_DELAY_MINUTES', '5'), { min: 0, max: 240 }), minH = num(A('MISSED_MIN_HOURS', '0'), { min: 0, max: 720 });
      collectors.push(() => {
        drop('DAILY_SCHEDULE'); drop('WEEKLY_SCHEDULE');
        Array.from(rows.children).map((r) => r.get()).filter((x) => x.on.some(Boolean)).forEach((x, n) => {
          const attrs = { ID: Date.now() + n, NAME: n ? 'Backup Schedule ' + (n + 1) : 'Backup Schedule', HOUR: x.hour, MINUTE: x.minute, DURATION: dur.value, BACKUP_TYPE: 'FILE', ENABLED_SKIP_BACKUP: 'N' };
          if (x.on.every(Boolean)) addEl('DAILY_SCHEDULE', Object.assign({ BACKUP_INTERVAL: -1 }, attrs));
          else { const w = {}; dayKeys.forEach((k, i) => { w[k] = x.on[i] ? 'Y' : 'N'; }); addEl('WEEKLY_SCHEDULE', Object.assign(w, attrs)); }
        });
        setA('RUN_MISSED', missed.input.checked ? 'Y' : 'N'); setA('RUN_MISSED_NET', missedNet.input.checked ? 'Y' : 'N'); setA('MISSED_DELAY_MINUTES', delay.value || 0); setA('MISSED_MIN_HOURS', minH.value || 0);
      });
      return h('div', { class: 'form' },
        fr(t('When'), t('Days, hours and minutes from a list — add more times for the same set'), rows, h('div', {}, btn('+ ' + t('Add a time'), () => { if (rows.children.length < 12) addRow(null); }, 'sm'))),
        fr(t('Stop the backup after'), t('What was sent is kept; the rest continues in the next run'), dur),
        fr(t('Missed backup'), t('The computer was off at the time'), missed, h('div', { class: 'inline' }, h('span', {}, t('After')), delay, h('span', {}, t('minutes; only if the last backup is older than')), minH, h('span', {}, t('hours (0 = always)')))),
        fr(t('Internet down'), t('The internet or the server was not reachable at the time, or the connection dropped in the middle of the backup. Checked every minute.'), missedNet));
    };
    // ---- method: incremental / differential, SQL full + differential + transaction logs
    build.method = () => {
      const dt = h('div', { class: 'opt' }, [['I', t('Incremental'), t('Only what changed since the last backup — the smallest (recommended)')], ['D', t('Differential'), t('Everything changed since the last full copy — a faster restore')]].map(([v, b, s]) => h('label', {}, h('input', { type: 'radio', name: 'dt', value: v, checked: A('DEFAULT_DELTA_TYPE', 'I') === v }), h('div', {}, h('b', {}, b), h('span', {}, s)))));
      const full = sel([['-1', t('A full backup every time')], ...[0, 1, 2, 3, 4, 5, 6].map((i) => [String(i), t('Full on {0}, differential on the other days', [t('Sunday'), t('Monday'), t('Tuesday'), t('Wednesday'), t('Thursday'), t('Friday'), t('Saturday')][i])])], A('SQL_FULL_DAY', '-1'));
      const logs = sel([['0', t('Off')], ...[15, 30, 60, 120, 240].map((x) => [String(x), t('Every {0} minutes', x)])], A('LOG_INTERVAL_MINUTES', '0'));
      collectors.push(() => { const c = dt.querySelector('input:checked'); if (c) setA('DEFAULT_DELTA_TYPE', c.value); if (type === 'MSSQL') { setA('SQL_FULL_DAY', full.value); setA('LOG_INTERVAL_MINUTES', logs.value); } });
      return h('div', { class: 'form' },
        restic ? fr(t('Method'), null, h('span', {}, t('Every backup sends only what changed, and every point in time is a full restore point.'))) : fr(t('Changes inside large files'), null, dt),
        type === 'MSSQL' ? fr(t('SQL Server'), t('Full and differential backups'), full) : null,
        type === 'MSSQL' ? fr(t('Transaction-log backups'), t('For a restore to an exact moment'), logs) : null);
    };
    // ---- destination: server, server + local copy, local only (DEST-010)
    build.dest = () => {
      const mode = A('DEST_MODE', 'SERVER');
      const lc = kids('EXTRA_LOCAL_BACKUP')[0];
      const radios = h('div', { class: 'opt' }, [['SERVER', t('The backup server'), t('Off site, encrypted (recommended)')], ['BOTH', t('The server + a local copy'), t('A fast restore without the internet from a disk or NAS')], ['LOCAL', t('Local only'), restic ? t('Only on a local disk or NAS') : t('Needs the restic engine (Windows 10 / Server 2016 or later)')]].map(([v, b, s]) =>
        h('label', {}, h('input', { type: 'radio', name: 'dm', value: v, checked: mode === v, disabled: v === 'LOCAL' && !restic }), h('div', {}, h('b', {}, b), h('span', {}, s)))));
      const p = inp(lc ? lc.getAttribute('BACKUP_TO') : 'E:\\LocalBackup', { class: 'ltr' }), days = num(lc ? lc.getAttribute('PERIOD') || 7 : 7, { min: 1, max: 3650 });
      collectors.push(() => {
        const m = radios.querySelector('input:checked').value; setA('DEST_MODE', m);
        drop('EXTRA_LOCAL_BACKUP'); addEl('EXTRA_LOCAL_BACKUP', { ENABLED: m === 'BOTH' ? 'Y' : 'N', ZIP: 'Y', BACKUP_TO: p.value, SKIP_OFFSITE_BACKUP: m === 'LOCAL' ? 'Y' : 'N', SET_LOCAL_COPY_PERMISSION: 'Y', ENABLE_RETENTION: 'Y', PERIOD: days.value || 7, UNIT: 'DAYS' });
      });
      return h('div', { class: 'form' }, fr(t('Where the backups go'), null, radios), fr(t('Local disk or network folder'), t('For the local copy or the local-only backup'), p), fr(t('Keep the local copy'), null, h('div', { class: 'inline' }, days, h('span', {}, t('days')))));
    };
    // ---- versions kept: by days or by number of backups (never unlimited), GFS
    build.ret = () => {
      const rp = kids('RETENTION_POLICY')[0];
      const unit = sel([['DAYS', t('Days')], ['JOBS', t('Backups')]], rp ? rp.getAttribute('UNIT') : 'DAYS'), period = num(rp ? rp.getAttribute('PERIOD') : 30, { min: 1 });
      const keep = (ty) => { const x = rp && Array.from(rp.children).find((c) => c.getAttribute('TYPE') === ty); return x ? x.getAttribute('KEEP') : '0'; };
      const gfs = [['DAILY', t('Daily')], ['WEEKLY', t('Weekly')], ['MONTHLY', t('Monthly')], ['QUARTERLY', t('Quarterly')], ['YEARLY', t('Yearly')]].map(([k, l]) => [k, l, num(keep(k), { min: 0 })]);
      collectors.push(() => {
        drop('RETENTION_POLICY'); const r = addEl('RETENTION_POLICY', { UNIT: unit.value, PERIOD: Math.max(1, Number(period.value) || 1) });
        for (const [k, , n] of gfs) if (Number(n.value) > 0) { const c = doc.createElement('RETENTION_SETTING'); c.setAttribute('TYPE', k); c.setAttribute('NAME', k); c.setAttribute('KEEP', n.value); c.setAttribute('ID', Date.now()); r.append(c); }
      });
      return h('div', { class: 'form' }, fr(t('Keep versions for'), t('By days or by number of backups — there is no unlimited'), h('div', { class: 'inline' }, period, unit)),
        fr(t('Long-term versions (GFS)'), t('0 = not used'), h('div', { class: 'grid g3' }, gfs.map(([, l, n]) => h('label', { class: 'inline' }, h('span', {}, l), n)))));
    };
    // ---- filters: starts with / ends with / contains / folder — back up or skip
    build.filter = () => {
      const rows = h('div', { class: 'paths' });
      const TY = [['START_WITH', t('Name starts with')], ['END_WITH', t('Name ends with')], ['CONTAIN', t('Name contains')], ['EXACT', t('Name is exactly')], ['WILDCARD', t('Pattern (* ?)')]];
      const add = (f) => { const act = sel([['N', t('Skip')], ['Y', t('Back up only')]], f.inc || 'N'), ty = sel(TY, f.type || 'START_WITH'), v = inp(f.pat || '', { class: 'ltr', placeholder: '~$' }), dirs = sel([['N', t('Files')], ['Y', t('Folders')]], f.dir || 'N');
        const r = h('div', { class: 'frow f5' }, act, ty, v, dirs, h('button', { class: 'btn sm danger', type: 'button', 'aria-label': t('Remove'), onclick: () => r.remove() }, '✕')); r.get = () => ({ inc: act.value, type: ty.value, pat: v.value.trim(), dir: dirs.value }); rows.append(r); };
      kids('FILTER').filter((f) => f.getAttribute('NAME') !== 'COMMON').forEach((f) => Array.from(f.children).filter((c) => c.tagName === 'PATTERN').forEach((p) => add({ inc: f.getAttribute('INCLUDE'), type: f.getAttribute('TYPE'), pat: p.textContent, dir: f.getAttribute('APPLY_DIR') })));
      collectors.push(() => { kids('FILTER').filter((f) => f.getAttribute('NAME') !== 'COMMON').forEach((f) => f.remove()); Array.from(rows.children).map((r) => r.get()).filter((x) => x.pat).forEach((x) => { const f = addEl('FILTER', { TYPE: x.type, TOP_DIR: '', INCLUDE: x.inc, ONLY: 'Y', APPLY_DIR: x.dir, APPLY_FILE: x.dir === 'Y' ? 'N' : 'Y', NAME: 'Filter', ID: Date.now() }); const p = doc.createElement('PATTERN'); p.textContent = x.pat; f.append(p); }); });
      return h('div', { class: 'form' }, fr(t('Filter rules'), t('For example: skip files whose name starts with ~$ (Office temporary files)'), rows, h('div', { class: 'inline' }, btn('+ ' + t('Add a rule'), () => add({}), 'sm'))));
    };
    // ---- REP-020: the set's own reports — every backup, restore and restore test of 60 days, with its log
    build.rep = () => {
      const box = h('div', { class: 'stack' }, h('p', { class: 'muted' }, t('Loading…')));
      (async () => {
        const r = await api('GET', path + '/runs'); const runs = list(r.runs);
        const backups = runs.filter((x) => x.kind === 'Backup'), ok = backups.filter((x) => x.status === 'ok').length;
        const lastOk = backups.find((x) => x.status === 'ok');
        const detail = h('div', {});
        const openRun = async (x) => {
          const lg = x.log ? await api('GET', 'logs?' + new URLSearchParams({ cat: x.kind === 'Backup' ? 'Backup' : 'Restore', login: u.login, set: S.set, file: x.log })) : null;
          detail.replaceChildren(card(t(KIND[x.kind] || x.kind) + ' · ' + when(x.time), statusPill(x.status), h('div', { class: 'stack' },
            h('div', { class: 'grid g4' }, kv(t('New'), x.new), kv(t('Changed'), x.upd), kv(t('Deleted'), x.del), kv(t('Sent'), size(x.bytes))),
            x.status === 'bad' || x.status === 'warn' ? aiExplain(Object.assign({ login: u.login }, x)) : null,
            lg ? h('div', { class: 'logbox' }, logTable(lg.text || '')) : null)));
        };
        box.replaceChildren(
          h('div', { class: 'grid g4' }, kpiBox(t('Backups — 60 days'), backups.length), kpiBox(t('Succeeded'), backups.length ? Math.round(ok * 100 / backups.length) + '%' : '—'),
            kpiBox(t('Last good backup'), lastOk ? when(lastOk.time) : t('Not yet')), kpiBox(t('Sent'), size(backups.reduce((a, x) => a + Number(x.bytes || 0), 0)))),
          table([t('When'), t('Kind'), t('Status'), t('New / changed / deleted'), t('Sent')], runs.map((x) => [N(when(x.time)), t(KIND[x.kind] || x.kind), statusPill(x.status), N((x.new || 0) + ' / ' + (x.upd || 0) + ' / ' + (x.del || 0)), N(size(x.bytes))]),
            (i) => openRun(runs[i]), { empty: t('Nothing here yet.') }), detail);
      })().catch((e) => box.replaceChildren(h('p', { class: 'bad' }, e.message)));
      return box;
    };
    // ---- encryption and compression
    build.enc = () => {
      const ek = kids('ENCRYPTING_KEY')[0];
      const comp = sel([['MAX', t('Maximum (default — the smallest backups)')], ['FAST', t('Fast')], ['NONE', t('None')]], A('COMPRESSION', 'MAX'));
      collectors.push(() => setA('COMPRESSION', comp.value));
      return h('div', { class: 'form' }, fr(t('Encryption'), t('AES-256 on the customer\'s computer, before anything is sent'), h('div', { class: 'note lock' }, '🔒', h('span', {}, t('Key: {0}. Only the customer holds it — it cannot be changed or read from here.', { PASSWORD: t('from the customer\'s password'), CUSTOM: t('a separate encryption password'), DEFAULT: t('a random key') }[ek ? ek.getAttribute('KEY_TYPE') : 'PASSWORD'] || '')))),
        fr(t('Compression'), null, comp));
    };
    // ---- resources: do not stall the computer (RES-010)
    build.perf = () => {
      const bw = num(A('BANDWIDTH_KBPS', '0'), { min: 0 }), busy = num(A('BUSY_CPU_PERCENT', '0'), { min: 0, max: 99 });
      const low = togRow(A('LOW_PRIORITY', 'Y') === 'Y', t('Low priority — the computer stays responsive'));
      collectors.push(() => { setA('BANDWIDTH_KBPS', bw.value || 0); setA('BUSY_CPU_PERCENT', busy.value || 0); setA('LOW_PRIORITY', low.input.checked ? 'Y' : 'N'); });
      return h('div', { class: 'form' }, fr(t('Upload limit'), t('KB/s, 0 = no limit'), h('div', { class: 'inline' }, bw, h('span', {}, 'KB/s'))),
        fr(t('Priority'), null, low), fr(t('Wait while the computer is busy'), t('CPU above this level (%) — 0 = never wait'), h('div', { class: 'inline' }, busy, h('span', {}, '%'))));
    };
    // ---- commands before / after
    build.cmd = () => {
      const pre = pathRows(kids('PRE_CMD').map((c) => c.getAttribute('PATH')), 'net stop MyApp'), post = pathRows(kids('POST_CMD').map((c) => c.getAttribute('PATH')), 'net start MyApp');
      const stop = togRow(kids('PRE_CMD').some((c) => c.getAttribute('STOP_ON_FAILURE') === 'Y'), t('Stop the backup when a command before it fails'));
      // only with a command before the backup (the choice is kept with the commands)
      const upd = () => { stop.hidden = !pre.values().length; }; upd(); pre.addEventListener('input', upd); pre.addEventListener('click', () => setTimeout(upd, 0));
      collectors.push(() => { drop('PRE_CMD'); drop('POST_CMD'); pre.values().forEach((v, i) => addEl('PRE_CMD', { ID: Date.now() + i, NAME: 'pre' + i, PATH: v, WORKING_DIR: '', STOP_ON_FAILURE: stop.input.checked ? 'Y' : 'N' })); post.values().forEach((v, i) => addEl('POST_CMD', { ID: Date.now() + 100 + i, NAME: 'post' + i, PATH: v, WORKING_DIR: '' })); });
      return h('div', { class: 'form' }, fr(t('Before the backup'), null, pre, stop), fr(t('After the backup'), null, post));
    };
    // ---- maintenance: back up now, stop, rebuild, check
    build.maint = () => card(t('Maintenance'), null, h('div', { class: 'stack' },
      h('div', { class: 'inline' }, btn('▶ ' + t('Back up now'), async () => { await api('POST', path + '/run'); toast(t('The backup was started on the customer\'s computer')); }), btn('■ ' + t('Stop'), async () => { await api('POST', path + '/stop'); toast(t('A stop request was sent')); })),
      h('div', { class: 'inline' }, btn(t('Check the data (verify)'), async () => { const r = await api('POST', 'verify', { login: u.login, set: S.set }); toast(t('Checked: {0} damaged', r.bad || 0)); }),
        btn(t('Rebuild the index'), async () => { if (!await confirmBox(t('Rebuild the index of this set from its data?'))) return; const r = await api('POST', 'rebuild', { login: u.login, set: S.set, verify: 1 }); toast(t('Rebuilt: {0} damaged', r.bad || 0)); }),
        btn('🗑 ' + t('Delete the set'), async () => { if (!await confirmBox(t('Delete the set? It moves to the recycle bin for 14 days.'))) return; const r = await api('POST', 'users/' + enc(u.login) + '/delete', { set: S.set }); toast(r.pending === '1' ? t('Another administrator must approve the deletion.') : t('Moved to the recycle bin')); done(); }, 'danger')),
      h('p', { class: 'muted small' }, t('A change of the settings reaches the computer within a minute.'))));

    const save = async () => {
      Object.keys(panes).forEach(() => { }); collectors.forEach((c) => c());
      await api('POST', path, { set: new XMLSerializer().serializeToString(E) });
    };
    const wrapper = h('div', {}, h('div', { class: 'crumbs' }, h('button', { type: 'button', onclick: done }, t('Backup sets')), '‹', A('NAME', '')),
      h('div', { class: 'head' }, h('div', { class: 't' }, h('h1', {}, A('NAME', '')), h('p', {}, (TYPES[type] ? t(TYPES[type]) : type) + ' · ' + list(d.computers).filter((c) => c.detached !== '1').map((c) => c.computer).join(', '))),
        h('div', { class: 'acts' }, btn('▶ ' + t('Back up now'), async () => { await api('POST', path + '/run'); toast(t('The backup was started on the customer\'s computer')); }), btn('■ ' + t('Stop'), async () => { await api('POST', path + '/stop'); toast(t('A stop request was sent')); }))),
      tabBar, h('section', { class: 'card' }, h('div', { class: 'cb' }, body), savebar(save, done, t('A change of the settings reaches the computer within a minute.'))));
    show(tabs.some(([k]) => k === S.stab) ? S.stab : 'general');
    return wrapper;
  }
  // rows of paths / commands that can be typed and edited, added and removed (PATHS-010)
  const COMMON_FILES = ['*.tmp', '~$*', 'Thumbs.db', 'desktop.ini', 'pagefile.sys', 'hiberfil.sys', 'swapfile.sys'];
  const COMMON_DIRS = ['$RECYCLE.BIN', 'System Volume Information', 'Temporary Internet Files', 'INetCache'];

  /**
   * SRC-030: the folders of the customer's computer as a tree (sent by the client software — names only), with the chosen
   * folders beside it: ✓ = back up with everything inside, ✕ = skip this folder inside a chosen one (Ahsay's
   * "selected / deselected", Acronis' items and exclusions). A folder not listed yet is asked from the computer.
   * inc / exc are changed in place.
   */
  function folderPicker(u, computer, inc, exc) {
    let data = { sep: '\\', dirs: [], at: '' };
    const opened = new Set(), asked = new Set();
    let kidsOf = new Map(), roots = [], polling = null;
    const win = () => data.sep !== '/';
    const key = (p) => (win() ? p.toLowerCase() : p);
    const cmp = (a, b) => a.localeCompare(b, undefined, { sensitivity: 'base', numeric: true });
    const trim = (p) => (p.length > 1 && /[\\/]$/.test(p) && !/^[A-Za-z]:\\$/.test(p) && p !== '/' ? p.slice(0, -1) : p);
    const under = (p, root) => { p = key(trim(p)); root = key(trim(root)); if (p === root) return true; return p.startsWith(/[\\/]$/.test(root) ? root : root + (root.includes('/') && !root.includes('\\') ? '/' : '\\')) || (root === '/' && p.startsWith('/')); };
    const below = (p, root) => under(p, root) && key(trim(p)) !== key(trim(root));
    const nearest = (p) => { let best = '', kind = null; inc.forEach((x) => { if (under(p, x) && x.length > best.length) { best = x; kind = 'inc'; } }); exc.forEach((x) => { if (under(p, x) && x.length >= best.length) { best = x; kind = 'exc'; } }); return kind; };
    const stateOf = (p) => { const k = nearest(p); if (k === 'inc') return exc.some((x) => below(x, p)) ? 'onpart' : 'on'; if (inc.some((x) => below(x, p))) return 'part'; return k === 'exc' ? 'exc' : 'off'; };
    const toggle = (p) => {
      const was = nearest(p) === 'inc';
      for (const arr of [inc, exc]) for (let i = arr.length - 1; i >= 0; i--) if (under(arr[i], p)) arr.splice(i, 1);
      const now = nearest(p) === 'inc';
      if (was && now) exc.push(p); else if (!was && !now) inc.push(p);
      paint();
    };
    const parentOf = (p) => {
      if (p === '/' || /^[A-Za-z]:\\?$/.test(p)) return null;
      if (p.startsWith('/')) { const i = p.lastIndexOf('/'); return i <= 0 ? '/' : p.slice(0, i); }
      if (p.startsWith('\\\\') && p.split('\\').filter(Boolean).length <= 2) return null;   // \\server\share
      const i = p.lastIndexOf('\\'); if (i < 0) return null;
      const par = p.slice(0, i); return /^[A-Za-z]:$/.test(par) ? par + '\\' : par;
    };
    const nameOf = (p) => { const par = parentOf(p); return par ? p.slice(par.length).replace(/^[\\/]/, '') : p; };
    const rebuild = () => {
      const all = new Map();
      const addP = (p) => { p = trim(p); if (!p || all.has(key(p))) return; all.set(key(p), p); const par = parentOf(p); if (par) addP(par); };
      data.dirs.forEach(addP); inc.forEach(addP); exc.forEach(addP);
      kidsOf = new Map(); roots = [];
      for (const p of all.values()) { const par = parentOf(p); if (!par) roots.push(p); else { const k = key(par); if (!kidsOf.has(k)) kidsOf.set(k, []); kidsOf.get(k).push(p); } }
      kidsOf.forEach((v) => v.sort(cmp)); roots.sort(cmp);
    };
    const load = async () => { const r = await api('GET', 'users/' + enc(u.login) + '/folders?computer=' + enc(computer)); data = { sep: r.sep || '\\', dirs: (r.dirs || '').split('\n').filter(Boolean), at: r.at || '' }; list(r.waiting).forEach((w) => asked.add(key(w.path || ''))); rebuild(); return r; };
    const ask = async (p) => {
      await api('POST', 'users/' + enc(u.login) + '/browse', { computer, path: p || '' }); asked.add(key(p || '')); paint();
      if (polling) return;
      const was = data.at; let n = 0;
      polling = setInterval(async () => {
        n++;
        try { if (!box.isConnected || n > 36) { clearInterval(polling); polling = null; return; } const r = await load(); if (String(r.at || '') !== String(was || '')) { clearInterval(polling); polling = null; asked.clear(); paint(); } } catch (e) { clearInterval(polling); polling = null; }
      }, 5000);
    };
    const ICON = { on: '✓', onpart: '✓', part: '–', exc: '✕', off: '' };
    const pathText = (p) => p.split(/(?<=[\\/])/).flatMap((x, i) => (i ? [h('wbr', {}), x] : [x]));
    const cb = (p) => { const st = stateOf(p); return h('button', { type: 'button', class: 'tcb ' + st, role: 'checkbox', 'aria-checked': st === 'on' || st === 'onpart' ? 'true' : st === 'part' ? 'mixed' : 'false', 'aria-label': p, title: st === 'exc' ? t('Skipped') : '', onclick: () => toggle(p) }, ICON[st]); };
    const nodeEl = (p) => {
      const k = key(p), kids = kidsOf.get(k) || [], open = opened.has(k);
      const el = h('div', { class: 'tnode' }, h('div', { class: 'trow' },
        h('button', { type: 'button', class: 'ttw', 'aria-expanded': String(open), 'aria-label': t('Open'), onclick: () => { if (open) opened.delete(k); else opened.add(k); paint(); } }, open ? '▾' : '▸'),
        cb(p), h('span', { class: 'tname ltr ' + stateOf(p) }, nameOf(p))));
      if (open) el.append(h('div', { class: 'tkids' }, kids.length ? kids.map(nodeEl) : h('div', { class: 'trow muted small' }, asked.has(k) ? t('Asked the computer — the folders arrive within a minute…') : h('button', { class: 'btn link sm', type: 'button', onclick: wrap(() => ask(p)) }, t('Show the sub-folders (asks the computer)')))));
      return el;
    };
    const find = inp('', { placeholder: t('Find a folder…'), class: 'ltr', type: 'search' });
    find.addEventListener('input', () => paint());
    const typed = inp('', { class: 'ltr', placeholder: data.sep === '/' ? '/home/data' : 'D:\\Data' });
    const addTyped = (to) => { const v = trim(typed.value.trim()); if (!v) { toast(t('Write the folder path first.'), true); typed.focus(); return; } if (to === inc) { toggle(v); if (nearest(v) !== 'inc') toggle(v); } else if (nearest(v) === 'inc') toggle(v); else if (!exc.some((x) => key(x) === key(v))) { exc.push(v); paint(); } typed.value = ''; };
    const head = h('div', { class: 'inline' });
    const treeBox = h('div', { class: 'ftree', role: 'tree' }), selBox = h('div', { class: 'tsel' });
    const box = h('div', { class: 'picker' }, head, h('div', { class: 'pick2' }, h('div', { class: 'stack' }, find, treeBox),
      h('div', { class: 'stack' }, h('b', {}, t('Chosen')), selBox, h('div', { class: 'inline' }, typed, btn('✓ ' + t('Back up'), () => addTyped(inc), 'sm'), btn('✕ ' + t('Skip'), () => addTyped(exc), 'sm')))));
    const paint = () => {
      head.replaceChildren(...[h('span', {}, '🖥 ', h('b', { class: 'ltr' }, computer || '—')),
        h('span', { class: 'muted small' }, data.at ? t('Folders as of {0}', when(data.at)) : computer ? t('The computer has not sent its folders yet — it sends them within a minute while the client software runs. You can also type a path.') : t('The set has no computer yet.')),
        computer ? btn('⟳ ' + t('Refresh the folders'), () => ask(''), 'sm') : null, asked.has('') ? h('span', { class: 'muted small' }, t('Asked the computer — the folders arrive within a minute…')) : null].filter(Boolean));
      const q = find.value.trim().toLowerCase();
      if (q.length >= 2) {
        const hits = data.dirs.filter((p) => nameOf(p).toLowerCase().includes(q)).sort(cmp).slice(0, 200);
        treeBox.replaceChildren(...(hits.length ? hits.map((p) => h('div', { class: 'trow' }, cb(p), h('span', { class: 'tname ltr ' + stateOf(p) }, ...pathText(p)))) : [h('p', { class: 'muted small' }, t('Nothing found.'))]));
      } else treeBox.replaceChildren(...(roots.length ? roots.map(nodeEl) : [h('p', { class: 'muted small' }, t('No folders yet.'))]));
      const chosen = inc.slice().sort(cmp);
      selBox.replaceChildren(...(chosen.length ? chosen.map((p) => h('div', { class: 'sgroup' },
        h('div', { class: 'srow' }, h('span', { class: 'tcb on' }, '✓'), h('span', { class: 'ltr grow' }, ...pathText(p)), h('button', { class: 'btn sm', type: 'button', 'aria-label': t('Remove'), title: t('Remove'), onclick: () => { for (const arr of [inc, exc]) for (let i = arr.length - 1; i >= 0; i--) if (under(arr[i], p)) arr.splice(i, 1); paint(); } }, '✕')),
        ...exc.filter((x) => below(x, p)).sort(cmp).map((x) => h('div', { class: 'srow sub' }, h('span', { class: 'tcb exc' }, '✕'), h('span', { class: 'ltr grow' }, ...pathText(x)), h('button', { class: 'btn sm', type: 'button', title: t('Back up again'), 'aria-label': t('Back up again'), onclick: () => { exc.splice(exc.indexOf(x), 1); paint(); } }, '↺'))))) : [h('p', { class: 'muted small' }, t('Nothing chosen yet.'))]));
    };
    rebuild(); inc.forEach((p) => { let par = parentOf(trim(p)); while (par) { opened.add(key(par)); par = parentOf(par); } }); roots.forEach((r) => opened.add(key(r))); paint();
    if (computer) load().then(() => { roots.forEach((r) => opened.add(key(r))); paint(); }).catch(() => {});
    return box;
  }

  function pathRows(values, placeholder) {
    const rows = h('div', { class: 'paths' });
    const add = (v, focus) => { const i = inp(v || '', { class: 'ltr', placeholder }); const r = h('div', { class: 'prow' }, i, h('button', { class: 'btn sm danger', type: 'button', 'aria-label': t('Remove'), onclick: () => r.remove() }, '✕')); rows.append(r); if (focus) i.focus(); };
    values.forEach((v) => add(v));
    const box = h('div', { class: 'stack' }, rows, h('div', {}, h('button', { class: 'btn sm', type: 'button', onclick: () => add('', true) }, '+ ' + t('Add a row'))));
    box.values = () => Array.from(rows.querySelectorAll('input')).map((i) => i.value.trim()).filter(Boolean);
    return box;
  }

  // ------------------------------------------------------------------ active backups (LIVE-010): double-click opens the customer's set
  PAGES.live = async () => {
    const [r] = await Promise.all([api('GET', 'live'), users()]);
    const rows = list(r.live);
    clearTimeout(S.liveTimer); S.liveTimer = setTimeout(() => { if (S.page === 'live') render(); }, 15000);
    return h('div', { class: 'stack' }, head(t('Active backups'), t('The backups running now on the customers\' computers. Double-click a row to open the customer\'s set. Refreshed every 15 seconds.')),
      h('section', { class: 'card' }, rows.length ? h('div', { class: 'tw' }, h('table', { class: 'cards' }, h('thead', {}, h('tr', {}, [t('Customer'), t('Computer'), t('Set'), t('Started'), t('Files'), t('Sent'), t('Progress'), ''].map((c) => h('th', {}, c)))),
        h('tbody', {}, rows.map((x) => h('tr', { class: 'click', ondblclick: () => go({ page: 'customer', login: x.login, ctab: 'sets', set: x.set, stab: 'general' }) },
          h('td', { 'data-l': t('Customer') }, h('b', {}, nameOf(x.login))), h('td', { 'data-l': t('Computer') }, x.computer || '—'), h('td', { 'data-l': t('Set') }, x.setName || '—', x.current ? h('span', { class: 'sub ltr' }, x.current) : null),
          h('td', { 'data-l': t('Started') }, N(when(x.started))), h('td', { 'data-l': t('Files') }, N(x.files || '0')), h('td', { 'data-l': t('Sent') }, N(size(x.bytes))),
          h('td', { 'data-l': t('Progress') }, Number(x.percent) >= 0 ? h('div', { class: 'prog' }, N(x.percent + '%'), bar(Number(x.percent), 'ok')) : h('span', { class: 'live' })),
          h('td', { 'data-l': '' }, h('button', { class: 'btn sm', type: 'button', title: t('Stop'), onclick: wrap(async (e) => { e.stopPropagation(); await api('POST', 'users/' + enc(x.login) + '/sets/' + x.set + '/stop'); toast(t('A stop request was sent')); }) }, '■'))))))) : h('div', { class: 'empty' }, t('No backup is running now.'))));
  };

  // ------------------------------------------------------------------ tasks of the last 24 hours (TASKS-010)
  PAGES.tasks = async () => {
    const [r] = await Promise.all([api('GET', 'tasks?hours=' + (S.hours || 24)), users()]);
    const all = list(r.tasks);
    const views = [['all', 'All'], ['ok', 'Succeeded'], ['warn', 'Warnings'], ['bad', 'Failed'], ['stopped', 'Stopped']];
    const v = views.some(([k]) => k === S.tv) ? S.tv : 'all';
    const rows = all.filter((x) => v === 'all' || x.status === v);
    const q = inp('', { class: 'search', placeholder: t('Filter: customer, computer, set…') });
    const holder = h('div', {}), detail = h('div', {});
    const draw = () => {
      const f = q.value.trim().toLowerCase(); const rs = rows.filter((x) => !f || [nameOf(x.login), x.login, x.computer, x.setName].join(' ').toLowerCase().includes(f));
      holder.replaceChildren(table([t('When'), t('Customer'), t('Computer'), t('Set'), t('Kind'), t('Status'), t('New / changed / deleted'), t('Sent')], rs.map((x) => [N(when(x.time)), h('b', {}, nameOf(x.login)), x.computer || '—', x.setName || '—', t(KIND[x.kind] || x.kind), statusPill(x.status),
        N((x.new || 0) + ' / ' + (x.upd || 0) + ' / ' + (x.del || 0)), N(size(x.bytes))]), (i) => openTask(rs[i]), { empty: t('No tasks in this view.') }));
    };
    const openTask = async (x) => {
      const lg = x.log ? await api('GET', 'logs?' + new URLSearchParams({ cat: x.kind === 'Backup' ? 'Backup' : 'Restore', login: x.login, set: x.set, file: x.log })) : null;
      detail.replaceChildren(card(nameOf(x.login) + ' · ' + (x.setName || '') + ' · ' + when(x.time), h('div', { class: 'inline' }, statusPill(x.status), btn(t('Open the set'), () => go({ page: 'customer', login: x.login, ctab: 'sets', set: x.set, stab: 'general' }), 'sm'), btn('▶ ' + t('Back up again'), async () => { await api('POST', 'users/' + enc(x.login) + '/sets/' + x.set + '/run'); toast(t('The backup was started on the customer\'s computer')); }, 'sm')),
        h('div', { class: 'stack' }, h('div', { class: 'grid g4' }, kv(t('New'), x.new), kv(t('Changed'), x.upd), kv(t('Deleted'), x.del), kv(t('Sent'), size(x.bytes))),
          x.status === 'bad' || x.status === 'warn' ? aiExplain(x) : null,
          lg ? h('div', { class: 'logbox' }, logTable(lg.text || '')) : null)));
      detail.scrollIntoView({ behavior: 'smooth' });
    };
    q.addEventListener('input', draw); draw();
    return h('div', { class: 'stack' }, head(t('Tasks — 24 hours'), t('Every backup, restore and restore test of the last {0} hours, with its status and details.', S.hours || 24),
      [sel([['24', t('24 hours')], ['72', t('3 days')], ['168', t('7 days')]], String(S.hours || 24))].map((s) => { s.classList.add('w-auto'); s.addEventListener('change', () => go({ hours: Number(s.value) })); return s; })),
      h('div', { class: 'grid g4' }, kpiBox(t('Tasks'), r.total || 0), kpiBox(t('Succeeded'), r.ok || 0), kpiBox(t('Warnings'), r.warn || 0), kpiBox(t('Failed'), r.bad || 0)),
      h('section', { class: 'card' }, h('div', { class: 'tools' }, h('div', { class: 'chips' }, views.map(([k, l]) => h('button', { type: 'button', class: 'chip' + (k === v ? ' on' : ''), onclick: () => go({ tv: k }) }, t(l) + ' (' + all.filter((x) => k === 'all' || x.status === k).length + ')'))), q), holder), detail);
  };
  // AI-020: the explanation of a failed run, written by the AI from its log
  function aiExplain(x) {
    const box = h('section', { class: 'ai-panel' }, h('div', { class: 'cb' }, h('div', { class: 'inline' }, pill('ai', '✦ AI'), btn(t('Explain with AI'), async () => {
      box.querySelector('.cb').replaceChildren(h('p', { class: 'muted' }, t('The AI is reading the log…')));
      const d = await api('POST', 'users/' + enc(x.login) + '/aidiagnose', { set: x.set, cat: x.kind === 'Backup' ? 'Backup' : 'Restore', file: x.log, lang: I18N.lang });
      box.querySelector('.cb').replaceChildren(h('div', { class: 'stack' }, h('b', {}, d.summary || ''), d.cause ? h('p', {}, h('b', {}, t('Likely cause') + ': '), d.cause) : null, list(d.steps).length ? h('ol', {}, list(d.steps).map((s) => h('li', {}, s.s))) : null, h('span', { class: 'muted small' }, t('Written by AI from the job log — check before acting.'))));
    }, 'sm'))));
    return box;
  }

  // ------------------------------------------------------------------ service calls (TICKETS-010: the rules of ITSguard)
  const TST = { New: ['New', 'info'], InProgress: ['In progress', 'info'], Waiting: ['Waiting for the customer', 'mut'], Deferred: ['Moved to a date', 'mut'], Resolved: ['Resolved', 'ok'], ToBill: ['To bill — not in the contract', 'warn'], Closed: ['Closed', 'ok'] };
  const TPR = { Urgent: ['Urgent', 'bad'], High: ['High', 'warn'], Normal: ['Normal', 'mut'], Low: ['Low', 'mut'] };
  const slaPill = (x) => { const m = Number(x.slaMinutes || 0), d = m >= 2880 ? t('{0} days', Math.round(m / 1440)) : m >= 60 ? t('{0} hours', Math.round(m / 60)) : t('{0} minutes', m);
    return ({ late: pill('bad', t('Late by {0}', d)), warn: pill('warn', t('{0} left', d)), ok: pill('ok', t('{0} left', d)), wait: pill('mut', x.status === 'Deferred' ? t('Back on {0}', whenIso(x.followUp)) : t('Waiting')), done: pill('mut', x.slaMet === '1' ? t('Met the SLA') : t('Missed the SLA')) })[x.sla] || pill('mut', '—'); };
  function ticketTable(rows) {
    return table([t('No.'), t('Customer'), t('Computer'), t('Subject'), t('Urgency'), t('Status'), t('Handler'), t('SLA')], rows.map((x) => [N('#' + x.id), h('b', {}, nameOf(x.login) || '—'), x.computer || '—', h('span', {}, x.subject, h('span', { class: 'sub' }, x.source === 'auto' ? t('Automatic — {0}', t(AUTO[x.kind] || x.kind || '')) : x.source === 'client' ? t('From the customer') : t('Manual'))),
      pill((TPR[x.priority] || [])[1] || 'mut', t((TPR[x.priority] || [x.priority])[0])), pill((TST[x.status] || [])[1] || 'mut', t((TST[x.status] || [x.status])[0])), x.assignee || '—', slaPill(x)]), (i) => go({ page: 'tickets', tck: rows[i].id }), { empty: t('No calls in this view.') });
  }
  const AUTO = { fail: 'backup failed', missed: 'backup did not run', warn: 'warnings', offline: 'not connected', quota: 'quota', restoretest: 'restore test failed', ransom: 'suspected ransomware' };
  PAGES.tickets = async () => {
    await users();
    if (S.tck) return ticketView(S.tck);
    const views = [['mine', 'Mine'], ['open', 'Open'], ['late', 'Late'], ['auto', 'Automatic'], ['waiting', 'Waiting / moved'], ['tobill', 'To bill'], ['closed', 'Closed'], ['all', 'All']];
    const v = views.some(([k]) => k === S.tv) ? S.tv : 'open';
    const r = await api('GET', 'tickets?scope=' + v);
    const q = inp('', { class: 'search', placeholder: t('Search: call number, customer, computer…') }), holder = h('div', {});
    const rows = list(r.tickets);
    const draw = () => { const f = q.value.trim().toLowerCase(); holder.replaceChildren(ticketTable(rows.filter((x) => !f || [x.id, x.login, nameOf(x.login), x.computer, x.subject].join(' ').toLowerCase().includes(f)))); };
    q.addEventListener('input', draw); draw();
    return h('div', { class: 'stack' }, head(t('Service calls'), t('Calls about the customers\' backups — opened by hand, by the customer in the software, or automatically from a backup problem.'), [btn('+ ' + t('New call'), () => go({ tck: 'new', newLogin: null }), 'pri')]),
      h('section', { class: 'card' }, h('div', { class: 'tools' }, h('div', { class: 'chips' }, views.map(([k, l]) => h('button', { type: 'button', class: 'chip' + (k === v ? ' on' : ''), onclick: () => go({ tv: k }) }, t(l)))), q), holder));
  };
  async function ticketView(id) {
    const isNew = id === 'new';
    const [x, staff] = await Promise.all([isNew ? Promise.resolve({ login: S.newLogin || '', priority: 'Normal', status: 'New' }) : api('GET', 'tickets/' + enc(id)), S.vendor ? Promise.resolve({ staff: [] }) : api('GET', 'staff')]);
    const back = () => go({ tck: null });
    const cust = sel([['', '—'], ...list(S.users).map((u) => [u.login, custName(u)])], x.login || '');
    const pcList = () => { const u = list(S.users).find((y) => y.login === cust.value); return u ? computersOf(u) : []; };
    const pc = sel([['', '—'], ...pcList().map((c) => [c, c])], x.computer || '');
    cust.addEventListener('change', () => { pc.replaceChildren(...[['', '—'], ...pcList().map((c) => [c, c])].map(([v2, l]) => h('option', { value: v2 }, l))); });
    const subject = inp(x.subject || ''), desc = h('textarea', {}, x.description || '');
    const prio = sel(Object.entries(TPR).map(([k, [l]]) => [k, t(l)]), x.priority), status = sel(Object.entries(TST).map(([k, [l]]) => [k, t(l)]), x.status);
    const handler = sel([['', '—'], ...list(staff.staff).map((s) => [s.login, s.name || s.login])], x.assignee || '');
    const localIso = (iso) => { if (!iso) return ''; const d = new Date(iso), p = (n) => String(n).padStart(2, '0'); return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + 'T' + p(d.getHours()) + ':' + p(d.getMinutes()); };
    const follow = inp(localIso(x.followUp), { type: 'datetime-local', class: 'w-auto' });
    const note = h('textarea', { placeholder: t('Add a note…') });
    const save = async () => { const r = await api('POST', 'tickets', { id: isNew ? '' : x.id, revision: x.rev, login: cust.value, computer: pc.value, subject: subject.value, description: desc.value, priority: prio.value, status: status.value, assignee: handler.value, followUp: follow.value ? new Date(follow.value).toISOString().slice(0, 16) : '', note: note.value, channel: x.channel || 'manual' }); if (isNew) S.tck = null; return r; };
    const notes = list(x.notes).map((n) => h('div', { class: 'ev' }, h('i', {}, n.user === 'system' ? '⚙' : '✎'), h('div', {}, h('b', {}, (n.user === 'system' ? t('System') : n.user) + ' · ', N(whenIso(n.time)), n.ip ? h('span', { class: 'muted small ltr' }, ' · ' + n.ip) : null), h('span', {}, noteText(n)))));
    return h('div', { class: 'stack' },
      head(isNew ? t('New call') : t('Call #{0}', x.id), isNew ? null : x.subject, isNew ? null : [x.set ? btn(t('Open the set'), () => go({ page: 'customer', login: x.login, ctab: 'sets', set: x.set, stab: 'general' })) : null, x.set ? btn('▶ ' + t('Run the backup'), async () => { await api('POST', 'users/' + enc(x.login) + '/sets/' + x.set + '/run'); toast(t('The backup was started on the customer\'s computer')); }) : null],
        [h('button', { type: 'button', onclick: back }, t('Service calls')), '‹', isNew ? t('New') : '#' + x.id]),
      h('div', { class: 'grid g21' },
        h('div', { class: 'stack' }, h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
          fr(t('Customer'), null, cust), fr(t('Computer'), null, pc), fr(t('Subject'), null, subject), fr(t('Description'), null, desc),
          fr(t('Urgency'), t('Sets the SLA time'), prio), fr(t('Status'), null, status), fr(t('Handler'), t('A call with no handler goes to the default handler'), handler),
          fr(t('Come back to it on'), t('With the status "Moved to a date"'), follow), fr(t('Note'), null, note))), savebar(save, back)),
          isNew ? null : card(t('Call log'), h('span', { class: 'muted small' }, t('Every change is written — who, when and from which address')), h('div', {}, notes))),
        h('div', { class: 'stack' }, isNew ? null : card('SLA', null, slaPill(x)),
          !isNew && x.source === 'auto' ? h('section', { class: 'ai-panel' }, h('div', { class: 'cb' }, h('div', { class: 'note' }, '↺', h('span', {}, t('When the backup succeeds, the call closes by itself (when nobody worked on it) or is marked "resolved".'))))) : null,
          isNew ? null : card(t('Actions'), null, btn('🗑 ' + t('Delete the call'), async () => { if (!await confirmBox(t('Delete call #{0}? It can be restored later.', x.id))) return; await api('POST', 'tickets/' + enc(x.id) + '/delete', { revision: x.rev }); back(); }, 'danger')))));
  }
  function noteText(n) {
    const st = (k) => t((TST[k] || [k || '—'])[0]), pr = (k) => t((TPR[k] || [k || '—'])[0]);
    switch (n.kind) {
      case 'open': return t('The call was opened');
      case 'status': return t('Status: {0} → {1}', st(n.from), st(n.to));
      case 'priority': return t('Urgency: {0} → {1}', pr(n.from), pr(n.to));
      case 'assign': return t('Handler: {0}', n.to || '—') + (n.text === 'default' ? ' (' + t('default handler') + ')' : '');
      case 'defer': return t('Moved to {0}', whenIso(n.to));
      case 'back': return t('The date came — the call is back in progress');
      case 'autoclose': return t('The backup succeeded — closed by itself');
      case 'restored': return t('Restored after being deleted');
      case 'change': return t('{0}: {1} → {2}', t(n.text), n.from || '—', n.to || '—');
      default: return n.text || '';
    }
  }

  // ------------------------------------------------------------------ administrators (TECH-010: administrators only)
  PAGES.admins = async () => {
    const r = await api('GET', 'staff');
    if (S.adm) return adminEditor(S.adm === 'new' ? {} : list(r.staff).find((x) => x.login === S.adm) || {});
    return h('div', { class: 'stack' }, head(t('Administrators'), t('Everyone who signs in to the admin site. Every administrator can do everything. Two-step verification is mandatory for all.'), [btn('+ ' + t('Administrator'), () => go({ adm: 'new' }), 'pri')]),
      h('section', { class: 'card' }, table([t('Name'), t('User name'), t('Two-step'), t('E-mail for calls'), t('Last sign-in')], list(r.staff).map((s) => [h('b', {}, s.name || s.login, s.main === '1' ? h('span', { class: 'sub' }, t('Main administrator')) : null), ltr(s.login), s.totp === '1' ? pill('ok', t('On')) : pill('warn', t('Set up at the first sign-in')), h('span', { class: 'mono' }, s.email || '—'), h('span', {}, N(when(s.lastLogin)), s.lastIp ? h('span', { class: 'sub ltr' }, s.lastIp) : null, s.locked === '1' ? pill('bad', t('Locked')) : null)]), (i) => go({ adm: list(r.staff)[i].login }))));
  };
  function adminEditor(s) {
    const isNew = !s.login, back = () => go({ adm: null });
    const login = inp(s.login || '', { class: 'ltr', disabled: !isNew }), name = inp(s.name || ''), pw = h('input', { type: 'password', autocomplete: 'new-password' });
    const m1 = inp(s.email || '', { class: 'ltr' }), m2 = inp(s.email2 || '', { class: 'ltr', placeholder: t('Another e-mail (optional)') });
    const notify = togRow(s.notifyAssign !== '0', t('E-mail when a call is assigned to them'));
    const act = (a, q) => btn(q, async () => { await api('POST', 'staff/' + enc(s.login) + '/' + a); toast(t('Done')); back(); }, 'sm');
    return h('div', { class: 'stack' }, head(s.name || s.login || t('New administrator'), null, null, [h('button', { type: 'button', onclick: back }, t('Administrators')), '‹', s.login || t('New')]),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        fr(t('Full name'), null, name), fr(t('User name'), null, login), fr(t('Password'), isNew ? t('At least 8 characters, with a letter') : t('Empty = no change'), pw),
        fr(t('E-mails'), t('For the calls assigned to them. Up to two.'), m1, m2), fr(t('Messages'), null, notify),
        fr(t('Two-step verification'), null, h('div', { class: 'note lock' }, '🔒', h('span', {}, t('Mandatory — set up at the first sign-in and cannot be switched off.')))))),
        savebar(async () => { if (s.main === '1') { toast(t('The main administrator is changed in the security settings.'), true); return false; } await api('POST', 'staff', { login: login.value, name: name.value, password: pw.value, email: m1.value, email2: m2.value, notifyAssign: notify.input.checked ? 1 : 0 }); }, back)),
      isNew || s.main === '1' ? null : card(t('Actions'), null, h('div', { class: 'inline' }, s.locked === '1' ? act('unlock', '🔓 ' + t('Unlock')) : null, act('resettotp', t('Reset two-step')), btn('🗑 ' + t('Delete'), async () => { if (!await confirmBox(t('Delete the administrator {0}?', s.login))) return; await api('POST', 'staff/' + enc(s.login) + '/delete'); back(); }, 'sm danger'))));
  }

  // ------------------------------------------------------------------ service call settings (TICKETS-020)
  PAGES.tset = async () => {
    const [s, staff] = await Promise.all([api('GET', 'ticketsettings'), api('GET', 'staff')]);
    const hours = ['Urgent', 'High', 'Normal', 'Low'].map((p) => [p, num(s['hours' + p], { min: 1, max: 2000 })]);
    const def = sel([['', t('— none —')], ...list(staff.staff).map((x) => [x.login, x.name || x.login])], s.defaultAssignee || '');
    const urg = (k) => sel(Object.entries(TPR).map(([v, [l]]) => [v, t(l)]), s['urgency' + k]);
    const rule = (label, key, opts, urgKey) => { const o = sel(opts, s[key]); const u2 = urgKey ? urg(urgKey) : null; const r = h('div', { class: 'inline' }, h('b', { style: 'min-width:170px' }, label), o, u2 ? h('span', { class: 'muted small' }, t('Urgency')) : null, u2); r.get = () => { const b = { [key]: o.value }; if (u2) b['urgency' + urgKey] = u2.value; return b; }; return r; };
    const rules = [rule(t('Backup failed'), 'fail', [['0', t('Off')], ['1', t('At the first failure')], ['2', t('2 failures in a row')], ['3', t('3 failures in a row')]], 'fail'),
      rule(t('Backup did not run'), 'missedhours', [['0', t('Off')], ['12', t('12 hours')], ['24', t('1 day')], ['48', t('2 days')], ['72', t('3 days')], ['168', t('1 week')]], 'missed'),
      rule(t('Repeated warnings'), 'warn', [['0', t('Off')], ['3', t('3 in a row')], ['5', t('5 in a row')], ['7', t('7 in a row')]], 'warn'),
      rule(t('Quota above'), 'quotapercent', [['0', t('Off')], ['80', '80%'], ['90', '90%'], ['95', '95%']], 'quota')];
    const flags = [['restoretest', t('A restore test failed')], ['ransom', t('Suspected ransomware (AI)')], ['autoclose', t('Close the automatic calls by themselves when the backup succeeds')], ['clientcalls', t('The customer can open calls from the software ("Help")')]].map(([k, l]) => [k, togRow(s[k] === '1', l)]);
    return h('div', { class: 'stack' }, head(t('Service call settings'), t('Calls open by themselves from backup problems and close by themselves when the backup succeeds. A customer can have thresholds of its own.')),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        fr(t('SLA time'), t('Hours, by urgency'), h('div', { class: 'grid g4' }, hours.map(([p, n]) => h('label', { class: 'inline' }, h('span', {}, t((TPR[p] || [p])[0])), n)))),
        fr(t('Default handler'), t('Receives a call with no handler'), def),
        fr(t('Open a call automatically when…'), null, h('div', { class: 'stack' }, rules)),
        fr(t('Options'), null, h('div', { class: 'stack' }, flags.map(([, r]) => r))))),
        savebar(async () => { const b = { defaultAssignee: def.value }; hours.forEach(([p, n]) => { b['hours' + p] = n.value; }); rules.forEach((r) => Object.assign(b, r.get())); flags.forEach(([k, r]) => { b[k] = r.input.checked ? 1 : 0; }); await api('POST', 'ticketsettings', b); }, () => go({ page: 'tickets' }))));
  };

  // ------------------------------------------------------------------ insights (AI)
  PAGES.ai = async () => {
    const [r, ck] = await Promise.all([api('GET', 'insights'), api('GET', 'checks'), users()]);
    const learned = list(r.learned), nLearned = learned.filter((x) => x.learned === '1').length;
    const minDisk = list(r.disks).map((d) => Number(d.days)).filter((x) => x > 0).sort((a, b) => a - b)[0];
    const days = (n) => pill(Number(n) < 14 ? 'bad' : 'warn', t('{0} days', n));
    return h('div', { class: 'stack' }, head(t('Insights (AI)'), t('What the AI sees across all customers. The forecasts and the ransomware learning work without an outside AI service.')),
      h('section', { class: 'ai-panel' }, h('div', { class: 'cb' }, h('div', { class: 'inline' }, h('span', { class: 'live' }), h('h2', {}, t('The AI is watching your backups')), h('span', { class: 'right' }, pill('ai', '✦ AI'))),
        h('div', { class: 'grid g4' }, kpiBox(t('backup sets learned what is normal'), nLearned + ' / ' + learned.length), kpiBox(t('computers at risk of missing a backup'), list(r.risk).length), kpiBox(t('customers near their quota'), list(r.quota).length), S.vendor ? null : kpiBox(t('until the first disk is full'), minDisk ? t('{0} days', minDisk) : '—')))),
      r.ai === '1' ? null : h('div', { class: 'note' }, 'ⓘ', h('span', {}, t('The forecasts and the ransomware learning work without the AI service. Turn on the AI assistant in Integrations for failure explanations and the restore search.'))),
      h('div', { class: 'grid g2' },
        card(t('Computers likely to miss their next backup'), null, table([t('Customer'), t('Set'), t('Risk'), t('Why')], list(r.risk).sort((a, b) => Number(b.risk) - Number(a.risk)).map((x) => [h('b', {}, x.alias || x.login), h('span', {}, x.setName, h('span', { class: 'sub' }, x.computer || '')), pill(Number(x.risk) >= 60 ? 'bad' : 'warn', x.risk + '%'), tr(x.why)]), null, { empty: t('None — every computer backs up on its usual rhythm.') })),
        card(t('Customers whose quota fills within 60 days'), null, table([t('Customer'), t('Quota'), t('Full in')], list(r.quota).map((q) => [h('b', {}, q.alias || q.login), N(size(q.used) + ' / ' + size(q.quota)), days(q.days)]), null, { empty: t('None — every quota has room for more than 60 days.') })),
        S.vendor ? null : card(t('Storage forecast'), null, table([t('Folder'), t('Free on disk'), t('Full in')], list(r.disks).map((d) => [ltr(d.path), N(size(d.free)), d.days ? days(d.days) : h('span', { class: 'muted nw' }, t('Not growing / still learning'))]), null, { empty: t('No storage folders.') })),
        card(t('Ransomware learning'), null, meter(t('Backup sets that learned what is normal'), nLearned, learned.length), h('p', { class: 'muted small' }, t('{0} of {1} backup sets have learned what is normal for them; the others use the fixed limits until they have 7 backups.', nLearned, learned.length)))),
      checksView(ck));
  };

  // CHK-010: the backup checks of the ITSguard analysis report — a click opens the set
  function checksView(ck) {
    const nm = (x) => h('b', {}, x.alias || nameOf(x.login));
    const open = (x) => go({ page: 'customer', login: x.login, ctab: 'sets', set: x.set, stab: 'rep' });
    const tbl = (cols, rows, cells, empty) => table(cols, rows.map(cells), (i) => open(rows[i]), { empty });
    const small = list(ck.small), still = list(ck.noChange), vol = list(ck.volume), ret = list(ck.shortRetention);
    const changes = list(ck.added).map((x) => Object.assign({ what: 'added' }, x)).concat(list(ck.removed).map((x) => Object.assign({ what: 'removed' }, x))).sort((a, b) => Number(b.day) - Number(a.day));
    const total = small.length + still.length + vol.length + ret.length;
    return h('div', { class: 'stack' },
      h('div', { class: 'inline' }, h('h2', {}, t('Backup checks')), total ? pill('warn', t('{0} to check', total)) : pill('ok', t('All clear'))),
      h('div', { class: 'grid g2' },
        card(t('Sets smaller than {0} MB', ck.smallMB), h('span', { class: 'muted small' }, t('Often a wrong path or a disconnected drive')),
          tbl([t('Customer'), t('Set'), t('Computer'), t('Original size')], small, (x) => [nm(x), x.name, h('span', { class: 'nw' }, x.computer || '—'), N(size(x.orig))], t('None.'))),
        card(t('No change in {0} days', ck.noChangeDays), h('span', { class: 'muted small' }, t('Backed up, but no file was new or changed — SQL too')),
          tbl([t('Customer'), t('Set'), t('Computer'), t('Backups'), t('Last backup')], still, (x) => [nm(x), h('span', {}, x.name, x.type === 'MSSQL' ? h('span', { class: 'sub' }, 'SQL Server') : null), h('span', { class: 'nw' }, x.computer || '—'), N(x.runs), N(when(x.last))], t('None.'))),
        card(t('A sharp change in size'), h('span', { class: 'muted small' }, t('{0}% or more since the day before', ck.volumePercent)),
          tbl([t('Customer'), t('Set'), t('Before'), t('Now'), t('Change')], vol, (x) => [nm(x), x.name, N(size(x.before)), N(size(x.after)), pill(Number(x.percent) < 0 ? 'bad' : 'warn', N((Number(x.percent) > 0 ? '+' : '') + x.percent + '%'))], t('None.'))),
        card(t('Versions kept less than {0} days', ck.minRetentionDays), null,
          tbl([t('Customer'), t('Set'), t('Computer'), t('Kept')], ret, (x) => [nm(x), x.name, h('span', { class: 'nw' }, x.computer || '—'), pill('warn', t('{0} days', x.days))], t('None.'))),
        card(t('Sets added or removed — {0} days', ck.changesDays), null,
          tbl([t('Day'), t('Customer'), t('Set'), ''], changes, (x) => [N(when(x.day)), nm(x), x.name, x.what === 'added' ? pill('ok', t('Added')) : pill('bad', t('Removed'))], t('None.')))));
  }

  // ------------------------------------------------------------------ logs
  async function logsView(fixed) {
    const us = list(S.users).length ? S.users : await users();
    const CATS = [['System', 'System'], ['Access', 'Connections and sign-ins'], ['Admin', 'Administrator actions'], ['BackupErrors', 'Backup errors'], ['Email', 'Mail'], ['Backup', 'Backup'], ['Restore', 'Restore'], ['Retention', 'Retention'], ['Rebuild', 'Rebuild']];
    const per = (c) => ['Backup', 'Restore', 'Retention', 'Rebuild'].includes(c);
    const cat = sel((fixed ? CATS.filter(([k]) => per(k)) : CATS).map(([k, l]) => [k, t(l)]), fixed ? 'Backup' : 'System');
    const user = sel(us.map((u) => [u.login, custName(u)]), fixed ? fixed.login : (us[0] || {}).login);
    const set = h('select', {}), file = h('select', {}), filter = inp('', { placeholder: t('Filter (e.g. err)') }), text = h('div', { class: 'logbox' });
    [cat, user, set, file, filter].forEach((x) => x.classList.add('w-auto'));
    if (fixed) user.classList.add('hide');
    const fillSets = () => { const u = us.find((x) => x.login === user.value); set.replaceChildren(...list(u && u.sets).map((s) => h('option', { value: s.id }, s.name))); };
    const load = async (keep) => {
      user.classList.toggle('hide', !!fixed || !per(cat.value)); set.classList.toggle('hide', !per(cat.value));
      const q = new URLSearchParams({ cat: cat.value, filter: filter.value }); if (per(cat.value)) { q.set('login', user.value); q.set('set', set.value || ''); } if (keep && file.value) q.set('file', file.value);
      const r = await api('GET', 'logs?' + q.toString()); const cur = r.file;
      file.replaceChildren(...list(r.files).map((x) => h('option', { value: x.name }, x.name))); if (cur) file.value = cur;
      text.replaceChildren(logTable(r.text || ''));
    };
    cat.addEventListener('change', wrap(() => load(false))); user.addEventListener('change', wrap(() => { fillSets(); return load(false); }));
    set.addEventListener('change', wrap(() => load(false))); file.addEventListener('change', wrap(() => load(true))); filter.addEventListener('change', wrap(() => load(true)));
    fillSets(); await load(false);
    return h('section', { class: 'card' }, h('div', { class: 'tools' }, cat, user, set, file, filter, btn(t('Refresh'), () => load(true), 'sm')), h('div', { class: 'cb' }, text));
  }
  // LOG-020: a log as rows — each line's level in colour (error, warning, success, start), alternate rows shaded
  const LEVELS = { err: ['bad', 'Error'], error: ['bad', 'Error'], warn: ['warn', 'Warning'], start: ['info', 'Start'], end: ['ok', 'End'], info: ['', 'Info'] };
  function csvCells(line) { const out = []; let cur = '', q = false; for (let i = 0; i < line.length; i++) { const c = line[i]; if (q) { if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; } else if (c === '"') q = true; else if (c === ',') { out.push(cur); cur = ''; } else cur += c; } out.push(cur); return out; }
  function sysLevel(msg) { const m = msg.toLowerCase(); return /error|failed|refused|denied|locked|invalid/.test(m) ? 'err' : /warn|frozen|suspect|late|missed/.test(m) ? 'warn' : /\bok\b|succeeded|success|approved|restored|registered/.test(m) ? 'end' : 'info'; }
  function logTable(text) {
    const lines = text.split('\n').map((x) => x.replace(/\r$/, '')).filter(Boolean);
    if (!lines.length) return h('p', { class: 'muted empty' }, t('(no entries)'));
    const rows = lines.map((line) => {
      // a backup log (Ahsay CSV: time in ms, level, …, message) or a server log (time, address, message)
      if (/^\d{12,},/.test(line)) {
        const c = csvCells(line); let lv = (c[1] || 'info').toLowerCase(); const msg = c[4] || '';
        if (lv === 'end' && msg && !/SUCCESS/.test(msg)) lv = /WARN/.test(msg) ? 'warn' : 'err';
        return { time: when(Number(c[0])), lv, ip: '', msg: msg || (c[1] === 'start' ? t('Start') : '') };
      }
      const f = line.split('\t');
      if (f.length >= 3) return { time: f[0], lv: sysLevel(f.slice(2).join(' ')), ip: f[1] === '-' ? '' : f[1], msg: f.slice(2).join(' ') };
      return { time: '', lv: sysLevel(line), ip: '', msg: line };
    });
    const hasIp = rows.some((r) => r.ip);
    return h('div', { class: 'tw' }, h('table', { class: 'logt' },
      h('thead', {}, h('tr', {}, [t('Time'), t('Level'), hasIp ? t('Address') : null, t('Details')].filter(Boolean).map((x) => h('th', {}, x)))),
      h('tbody', {}, rows.map((r) => { const L = LEVELS[r.lv] || LEVELS.info; return h('tr', { class: 'lv-' + (L[0] || 'info') },
        h('td', { class: 'nw' }, N(r.time)), h('td', {}, L[0] ? pill(L[0], t(L[1])) : h('span', { class: 'muted small' }, t(L[1]))), hasIp ? h('td', { class: 'ltr small muted' }, r.ip) : null, h('td', { class: 'msg' }, r.msg)); }))));
  }
  PAGES.logs = async () => h('div', { class: 'stack' }, head(t('Logs'), t('Everything that happened on the server. Every record has the time, the user and the address.')), await logsView(null));

  // ------------------------------------------------------------------ reports and restore tests
  PAGES.reports = async () => {
    const us = await users();
    const who = sel(us.map((u) => [u.login, custName(u)]), (us[0] || {}).login);
    const csv = () => { const rows = [['customer', 'login', 'computers', 'sets', 'quota_gb', 'counted_by', 'used_compressed_gb', 'used_original_gb']].concat(us.map((u) => [custName(u), u.login, computersOf(u).length, list(u.sets).length, (Number(u.quota) / 1073741824).toFixed(1), u.quotaType, ((Number(u.dataSize) + Number(u.retainSize)) / 1073741824).toFixed(2), ((Number(u.dataOrig) + Number(u.retainOrig)) / 1073741824).toFixed(2)]));
      const a = h('a', { href: URL.createObjectURL(new Blob(['\ufeff' + rows.map((r) => r.map((c) => '"' + String(c).replace(/"/g, '""') + '"').join(',')).join('\r\n')], { type: 'text/csv' })), download: 'usage-' + new Date().toISOString().slice(0, 10) + '.csv' }); document.body.append(a); a.click(); a.remove(); };
    return h('div', { class: 'stack' }, head(t('Reports')), h('div', { class: 'grid g2' },
      card(t('Data-protection report and restore certificate'), null, h('p', { class: 'muted' }, t('For the customer and for cyber insurance: GDPR, Israeli privacy law Amendment 13, ISO 27001.')), h('div', { class: 'inline' }, who, btn('⎙ ' + t('Open'), () => complianceReport(who.value), 'pri'))),
      card(t('Usage and billing'), null, h('p', { class: 'muted' }, t('Storage, computers and sets of every customer — for invoices (compressed and original size).')), h('div', {}, btn('⬇ ' + t('Download (CSV)'), csv)))));
  };
  PAGES.restoretests = async () => {
    const us = await users();
    const rows = us.flatMap((u) => list(u.sets).map((s) => ({ u, s })));
    const passed = rows.filter((x) => (x.s.restoreTest || '').startsWith('OK')).length, failed = rows.filter((x) => (x.s.restoreTest || '').startsWith('FAILED')).length;
    return h('div', { class: 'stack' }, head(t('Restore tests'), t('Every set is tested automatically every month: a sample is restored and compared with the original. This is how you know the backup can really be restored.')),
      h('div', { class: 'grid g3' }, kpiBox(t('Passed'), passed), kpiBox(t('Failed'), failed), kpiBox(t('Not tested yet'), rows.length - passed - failed)),
      h('section', { class: 'card' }, table([t('Customer'), t('Set'), t('Last test'), t('Result')], rows.map(({ u, s }) => [h('b', {}, custName(u)), s.name, N(when(s.lastRestoreTest)), s.restoreTest ? (s.restoreTest.startsWith('OK') ? pill('ok', t('Passed') + ' · ' + s.restoreTest.slice(3)) : pill('bad', t('Failed') + ' · ' + s.restoreTest.slice(7))) : pill('mut', t('Not yet'))]))));
  };

  // ------------------------------------------------------------------ storage on the server: drives, second server, recycle bin
  PAGES.storage = async () => {
    const [s, rb, del, cb] = await Promise.all([api('GET', 'settings'), api('GET', 'recycle'), api('GET', 'deletes'), api('GET', 'configbackup')]);
    const homes = list(s.homes);
    // CFGBK-010: the server's own settings, every day
    const cbCopy = inp(cb.copyTo, { class: 'ltr', placeholder: 'E:\\ServerSettings' });
    const download = async (name) => { const r = await fetch('/api/admin/configbackup/' + enc(name), { headers: { 'X-Session': S.session } }); if (!r.ok) throw new Error(t('Error {0}', r.status)); const a = h('a', { href: URL.createObjectURL(await r.blob()), download: name }); document.body.append(a); a.click(); a.remove(); };
    const cfgCard = h('section', { class: 'card' }, h('div', { class: 'ch' }, h('h2', {}, t('Backup of the server settings')), btn('⟳ ' + t('Back up now'), async () => { await api('POST', 'configbackup/now'); toast(t('Saved')); render(); }, 'sm')),
      h('div', { class: 'cb' }, h('div', { class: 'stack' }, h('p', { class: 'muted small' }, t('Every day: the server settings, contract, policies, service calls and every customer\'s profile and computers — never the backups themselves. The last {0} are kept.', cb.keep)),
        h('div', { class: 'form' }, fr(t('A copy in another folder'), t('A second disk or a network folder. Empty = no copy.'), cbCopy)),
        table([t('Backup'), t('Size'), ''], list(cb.backups).slice(0, 10).map((x) => [h('span', {}, N(when(x.time)), h('span', { class: 'sub ltr small' }, x.name)), N(size(x.size)), btn('⇩ ' + t('Download'), () => download(x.name), 'sm')]), null, { empty: t('Nothing here yet.') }))),
      savebar(async () => { await api('POST', 'configbackup', { copyTo: cbCopy.value }); }, () => go({ page: 'dash' })));
    const newPath = inp('', { class: 'ltr', placeholder: 'E:\\Backups' });
    const repUrl = inp(s.replicationUrl, { class: 'ltr', placeholder: 'https://second-server:8443' }), repTok = h('input', { type: 'password', placeholder: s.replicationHasToken === '1' ? t('(saved — empty = no change)') : '' }), repOn = togRow(s.replicationOn === '1', t('Copy every backup to a second server'));
    return h('div', { class: 'stack' }, head(t('Storage on the server'), t('Where the backups are kept: drives, a copy on a second server, and the recycle bin.')),
      card(t('Drives for backups'), null, table([t('Folder'), t('Customers'), t('Used'), t('Free')], homes.map((x) => [ltr(x.path), N(x.users || '0'), N(size(x.allocated || x.used)), N(size(x.free))])),
        h('div', { class: 'inline' }, newPath, btn('+ ' + t('Add a drive'), async () => { if (!newPath.value.trim()) throw new Error(t('Write the folder path first.')); await api('POST', 'settings', { homesSet: 1, homes: homes.map((x) => ({ path: x.path, maxQps: x.maxQps })).concat([{ path: newPath.value.trim() }]) }); toast(t('Saved')); render(); }, 'sm')),
        h('p', { class: 'muted small' }, t('A new customer is created on the drive with the most room.'))),
      h('section', { class: 'card' }, h('div', { class: 'ch' }, h('h2', {}, t('A copy on a second server'))), h('div', { class: 'cb' }, h('div', { class: 'form' }, fr(t('Disaster recovery'), null, repOn), fr(t('Address of the second server'), null, repUrl), fr(t('Access token'), null, repTok),
        fr(t('Waiting to copy'), null, N(s.replicationPending || '0')))), savebar(async () => { await api('POST', 'settings', { replicationOn: repOn.input.checked ? 1 : 0, replicationUrl: repUrl.value, replicationToken: repTok.value || undefined }); }, () => go({ page: 'dash' }))),
      cfgCard,
      card(t('Deletions waiting for approval'), h('span', { class: 'muted small' }, t('Another administrator approves — a stolen password alone cannot wipe a customer.')),
        table([t('What'), t('Asked by'), t('When'), ''], list(del.requests).map((r) => [h('b', {}, r.kind === 'user' ? t('Customer {0}', r.name) : t('Set {0} of {1}', r.name, r.login)), r.by, N(when(r.time)), h('div', { class: 'inline' }, btn(t('Approve'), async () => { await api('POST', 'deletes/' + r.id + '/approve'); toast(t('Deleted — in the recycle bin for 14 days')); render(); }, 'sm pri'), btn(t('Cancel'), async () => { await api('POST', 'deletes/' + r.id + '/cancel'); render(); }, 'sm'))]), null, { empty: t('None.') })),
      card(t('Recycle bin'), h('span', { class: 'muted small' }, t('Erased after 14 days')), table([t('What'), t('Deleted by'), t('When'), t('Erased on'), ''], list(rb.items).map((r) => [h('b', {}, r.kind === 'user' ? t('Customer {0}', r.login) : t('Set {0} of {1}', r.name || r.set, r.login)), r.by, N(when(r.time)), N(when(r.erase)), btn(t('Restore'), async () => { await api('POST', 'recycle/' + enc(r.id) + '/restore'); toast(t('Restored')); render(); }, 'sm')]), null, { empty: t('The recycle bin is empty.') })));
  };

  // ------------------------------------------------------------------ licence
  PAGES.license = async () => {
    const [lic, d] = await Promise.all([api('GET', 'license'), api('GET', 'dashboard')]);
    const key = h('textarea', { class: 'ltr', placeholder: t('Paste the licence you received here') });
    return h('div', { class: 'stack' }, head(t('Licence'), t('What was bought, what is in use and what is free.')),
      h('div', { class: 'grid g3' }, card(t('Customers'), null, meter(t('In use'), Number(lic.users || d.customers), Number(lic.maxUsers))), card(t('Computers'), null, meter(t('In use'), Number(lic.devices), Number(lic.maxDevices))), card(t('Storage'), null, meter(t('In use'), Number(d.usedBytes), Number(lic.maxStorageGB) * 1073741824, size))),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        fr(t('Edition'), null, lic.edition === 'FREE' ? h('span', {}, pill('info', t('Free edition')), ' ', t('up to 10 computers and 500 GB, no time limit')) : h('span', {}, pill('ok', lic.edition), ' ', lic.company || '', ' · ', t('valid until {0}', lic.expires))),
        fr(t('Server ID'), t('Send it to the software vendor'), h('span', { class: 'mono' }, lic.serverId)),
        fr(t('Update licence'), t('After buying more computers, customers or storage: brings the new licence from the software vendor'),
          btn(t('Update licence'), async () => { const r = await api('GET', 'license?update=1'); toast(r.updated === 'UPDATED' ? t('Licence updated: {0}', r.edition) : r.updated === 'CURRENT' ? t('The licence is up to date') : t('No licensing centre is known for this server — paste the new licence below'), r.updated === 'NO_SOURCE'); render(); }, 'sm')),
        lic.online ? fr(t('Licensing centre'), null, h('div', { class: 'inline' }, pill(lic.online === 'OK' ? 'ok' : 'warn', lic.online === 'OK' ? t('Connected') : lic.online), btn(t('Check now'), async () => { await api('GET', 'license?check=1'); render(); }, 'sm'))) : null,
        fr(t('New licence'), null, key))), savebar(async () => { if (!key.value.trim()) return; const r = await api('POST', 'license', { key: key.value }); toast(t('Licence accepted: {0}', r.edition)); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ resellers (vendors)
  const BRAND = [['PRODUCT', 'Product name'], ['SLOGAN', 'Slogan (optional)'], ['COMPANY', 'Company name'], ['PHONE', 'Phone'], ['EMAIL', 'E-mail (also for alerts)'], ['WEBSITE', 'Website'], ['COLOR', 'Color (#RRGGBB)'], ['ACCENT', 'Action color (#RRGGBB)'], ['LANGUAGE', 'Language of e-mails and client software']];
  const brandInput = (k, v) => k === 'LANGUAGE' ? sel(Object.entries(I18N.names).map(([c, n]) => [c, n]), v || 'en') : k === 'COLOR' || k === 'ACCENT' ? inp(v || '', { type: 'color' }) : inp(v || '', { class: k === 'PRODUCT' || k === 'COMPANY' || k === 'SLOGAN' ? '' : 'ltr' });
  function logoInput(current) {
    const img = h('img', { alt: '', class: current ? '' : 'hide', style: 'max-height:48px;background:#fff;border-radius:8px' }); if (current) img.src = current;
    const file = h('input', { type: 'file', accept: 'image/png,image/jpeg' }); const box = h('div', { class: 'stack' }, img, file); box.value = current || '';
    file.addEventListener('change', () => { const f = file.files[0]; if (!f) return; if (f.size > 200000) { toast(t('The logo is too large (up to 200 KB)'), true); file.value = ''; return; } const r = new FileReader(); r.onload = () => { box.value = r.result; img.src = r.result; img.classList.remove('hide'); }; r.readAsDataURL(f); });
    return box;
  }
  PAGES.resellers = async () => {
    const r = await api('GET', 'vendors');
    const edit = (v) => { v = v || {}; const id = inp(v.id || '', { class: 'ltr', disabled: !!v.id }), name = inp(v.name || ''), mu = num(v.maxUsers || 0, { min: 0 }), mq = num(v.maxQuotaGB || 0, { min: 0 }); const b = BRAND.map(([k]) => brandInput(k, v['brand' + k]));
      dialog(v.id ? t('Reseller — {0}', v.name || v.id) : t('New reseller'), h('div', { class: 'form' }, fr(t('ID (lowercase Latin letters)'), null, id), fr(t('Name'), null, name), fr(t('Maximum users (0 = no limit)'), null, mu), fr(t('Total quota GB (0 = no limit)'), null, mq), BRAND.map(([, l], i) => fr(t(l), null, b[i]))),
        async () => { const body = { id: id.value, name: name.value, maxUsers: mu.value, maxQuotaGB: mq.value }; BRAND.forEach(([k], i) => { body['brand' + k] = b[i].value; }); await api('POST', 'vendors', body); toast(t('Saved')); render(); }); };
    const addAdmin = (v) => { const l = inp('', { class: 'ltr' }), p = h('input', { type: 'password', autocomplete: 'new-password' }); dialog(t('New administrator — {0}', v.name || v.id), h('div', { class: 'form' }, fr(t('User name'), null, l), fr(t('Password (at least 8 characters, with a letter)'), null, p)), async () => { await api('POST', 'vendors/' + v.id + '/admins', { login: l.value, password: p.value }); toast(t('Administrator added')); render(); }); };
    return h('div', { class: 'stack' }, head(t('Resellers'), t('Each reseller (IT company) sees only its own customers, with its own branding in reports and alerts'), [btn('+ ' + t('New reseller'), () => edit(null), 'pri')]),
      h('section', { class: 'card' }, table([t('Reseller'), t('Users'), t('Total quota'), t('Administrators'), t('Status'), ''], list(r.vendors).map((v) => [h('b', {}, v.name || v.id, h('span', { class: 'sub ltr' }, v.id)), N((v.users || '0') + (Number(v.maxUsers) > 0 ? ' / ' + v.maxUsers : '')), Number(v.maxQuotaGB) > 0 ? N(v.maxQuotaGB + ' GB') : t('unlimited'), list(v.admins).map((a) => a.login).join(', ') || '—', v.disabled === '1' ? pill('bad', t('Disabled')) : pill('ok', t('Active')),
        h('div', { class: 'inline' }, btn(t('Edit'), () => edit(v), 'sm'), btn('+ ' + t('Administrator'), () => addAdmin(v), 'sm'), btn(v.disabled === '1' ? t('Enable') : t('Disable'), async () => { await api('POST', 'vendors', { id: v.id, disabled: v.disabled === '1' ? '0' : '1' }); render(); }, 'sm'))]))));
  };

  // ------------------------------------------------------------------ policies and templates (TPL-010)
  PAGES.policies = async () => {
    const r = await api('GET', 'templates');
    const edit = (tp) => {
      const s = tp ? new DOMParser().parseFromString(tp.set, 'application/xml').documentElement : null;
      const name = inp(tp ? tp.name : ''), type = sel(Object.entries(TYPES).map(([k, l]) => [k, t(l)]), tp ? tp.type : 'FILE');
      const sch = s && (s.querySelector('DAILY_SCHEDULE') || s.querySelector('WEEKLY_SCHEDULE'));
      const hh = sel(Array.from({ length: 24 }, (_, i) => [String(i), String(i).padStart(2, '0')]), sch ? sch.getAttribute('HOUR') : '22'), mm = sel(Array.from({ length: 12 }, (_, i) => [String(i * 5), String(i * 5).padStart(2, '0')]), sch ? sch.getAttribute('MINUTE') : '0');
      const rp = s && s.querySelector('RETENTION_POLICY'); const unit = sel([['DAYS', t('Days')], ['JOBS', t('Backups')]], rp ? rp.getAttribute('UNIT') : 'DAYS'), period = num(rp ? rp.getAttribute('PERIOD') : 30, { min: 1 });
      const comp = sel([['MAX', t('Maximum (default — the smallest backups)')], ['FAST', t('Fast')], ['NONE', t('None')]], s ? s.getAttribute('COMPRESSION') || 'MAX' : 'MAX');
      const bw = num(s ? s.getAttribute('BANDWIDTH_KBPS') || 0 : 0, { min: 0 });
      dialog(tp ? t('Template — {0}', tp.name) : t('New template'), h('div', { class: 'form' }, fr(t('Name'), null, name), fr(t('Set type'), null, type), fr(t('Time'), null, h('div', { class: 'inline ltr' }, hh, h('b', {}, ':'), mm)), fr(t('Keep versions for'), null, h('div', { class: 'inline' }, period, unit)), fr(t('Compression'), null, comp), fr(t('Upload limit'), 'KB/s', bw)),
        async () => { const x = '<BACKUP_SET ID="template" NAME="' + name.value.replace(/[<>&"]/g, '') + '" TYPE="' + type.value + '" COMPRESSION="' + comp.value + '" BANDWIDTH_KBPS="' + (bw.value || 0) + '"><DAILY_SCHEDULE HOUR="' + hh.value + '" MINUTE="' + mm.value + '" DURATION="-1"/><RETENTION_POLICY UNIT="' + unit.value + '" PERIOD="' + (period.value || 30) + '"/></BACKUP_SET>';
          await api('POST', 'templates', { name: name.value, type: type.value, set: x }); toast(t('Saved')); render(); });
    };
    return h('div', { class: 'stack' }, head(t('Policies and templates'), t('Set templates: schedule, versions kept, compression and resources for many customers at once — never their sources.'), [btn('+ ' + t('New template'), () => edit(null), 'pri')]),
      h('section', { class: 'card' }, table([t('Template'), t('Set type'), ''], list(r.templates).map((tp) => [h('b', {}, tp.name), TYPES[tp.type] ? t(TYPES[tp.type]) : tp.type, h('div', { class: 'inline' }, btn(t('Edit'), () => edit(tp), 'sm'), btn(t('Delete'), async () => { await api('POST', 'templates/' + enc(tp.name) + '/delete'); render(); }, 'sm danger'))]), null, { empty: t('No templates yet.') })),
      h('div', { class: 'note' }, 'ⓘ', h('span', {}, t('Apply a template from "Customers and sets": choose customers, then "Apply a template".'))));
  };

  // ------------------------------------------------------------------ defaults for new customers (DEF-010)
  PAGES.defaults = async () => {
    const d = await api('GET', 'defaults');
    const on = (k) => d[k] === '1';
    const quota = num(d.quotaGB, { min: 1 }), qt = sel([['COMPRESSED', t('By the size on the server (after compression)')], ['UNCOMPRESSED', t('By the original size')]], d.quotaType), sets = num(d.maxSets, { min: 1, max: 100 });
    const totp = togRow(on('requireTotp'), t('The customer must use two-step verification')), key = togRow(on('saveKey'), t('Keep the encryption key on the server for recovery'));
    const rights = RIGHTS.map(([k, label]) => togRow(on(k), t(label)));
    const hh = sel(Array.from({ length: 24 }, (_, i) => [String(i), String(i).padStart(2, '0')]), d.hour), mm = sel(Array.from({ length: 12 }, (_, i) => [String(i * 5), String(i * 5).padStart(2, '0')]), String(Math.round(Number(d.minute) / 5) * 5 % 60));
    const days = num(d.retentionDays, { min: 1, max: 3650 }), logs = num(d.logDays, { min: 7, max: 3650 });
    const comp = sel([['MAX', t('Maximum (default — the smallest backups)')], ['FAST', t('Fast')], ['NONE', t('None')]], d.compression), bw = num(d.bandwidth, { min: 0 });
    const vss = togRow(on('vss'), t('Shadow copy (open files, Windows)')), missed = togRow(on('runMissed'), t('A backup missed while the computer was off runs when it is back')), net = togRow(on('runMissedNet'), t('A backup missed because the internet was down starts when it is back'));
    const skip = togRow(on('skipSystem'), t('Recycle bin, temporary files, page file, Thumbs.db, ~$ Office files'));
    const sqlFull = sel([['-1', t('A full backup every time')], ...[0, 1, 2, 3, 4, 5, 6].map((i) => [String(i), t('Full on {0}, differential on the other days', [t('Sunday'), t('Monday'), t('Tuesday'), t('Wednesday'), t('Thursday'), t('Friday'), t('Saturday')][i])])], d.sqlFullDay || '-1');
    return h('div', { class: 'stack' }, head(t('Defaults for new customers'), t('What a new customer and each new backup set start with. Existing customers and sets do not change — for them use a template.')),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        h('h3', {}, t('A new customer')),
        fr(t('Quota'), null, h('div', { class: 'inline' }, quota, h('span', {}, 'GB'), qt)),
        fr(t('Backup sets'), t('At most'), sets),
        fr(t('Security'), null, totp, key),
        fr(t('What the customer may change'), t('In the client software. The rest is locked and managed here.'), h('div', { class: 'stack' }, rights)),
        h('h3', {}, t('A new backup set')),
        fr(t('Time'), t('When the customer did not choose a time'), h('div', { class: 'inline ltr' }, hh, h('b', {}, ':'), mm)),
        fr(t('Keep versions for'), null, h('div', { class: 'inline' }, days, h('span', {}, t('days')))),
        fr(t('Keep the backup logs for'), null, h('div', { class: 'inline' }, logs, h('span', {}, t('days')))),
        fr(t('Compression'), null, comp),
        fr(t('Upload limit'), t('KB/s, 0 = no limit'), h('div', { class: 'inline' }, bw, h('span', {}, 'KB/s'))),
        fr(t('Options'), null, vss, missed, net),
        fr(t('Skip system and temporary files'), null, skip),
        fr(t('SQL Server'), t('Large databases: a weekly full backup and differential backups the other days need less time and temporary space'), sqlFull))),
        savebar(async () => { const b = { quotaGB: quota.value, quotaType: qt.value, maxSets: sets.value, requireTotp: totp.input.checked ? 1 : 0, saveKey: key.input.checked ? 1 : 0, hour: hh.value, minute: mm.value, retentionDays: days.value, logDays: logs.value, compression: comp.value, bandwidth: bw.value || 0, vss: vss.input.checked ? 1 : 0, runMissed: missed.input.checked ? 1 : 0, runMissedNet: net.input.checked ? 1 : 0, skipSystem: skip.input.checked ? 1 : 0, sqlFullDay: sqlFull.value };
          RIGHTS.forEach(([k], i) => { b[k] = rights[i].input.checked ? 1 : 0; }); await api('POST', 'defaults', b); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ e-mails and alerts
  PAGES.notify = async () => {
    const s = await api('GET', 'settings');
    const sn = inp(s.senderName), se = inp(s.senderEmail, { class: 'ltr' });
    const smtpRows = h('div', { class: 'stack' });
    const addSmtp = (x) => { const host = inp(x.host || '', { class: 'ltr', placeholder: 'smtp.office365.com' }), port = num(x.port || 587), secu = sel([['STARTTLS', 'STARTTLS'], ['SSL', 'SSL'], ['NONE', t('None')]], x.security || 'STARTTLS'), login = inp(x.login || '', { class: 'ltr', placeholder: t('User') }), pw = h('input', { type: 'password', placeholder: x.hasPassword === '1' ? t('(saved — empty = no change)') : t('Password') });
      const r = h('div', { class: 'grid g4' }, host, port, secu, login, pw, h('button', { class: 'btn sm danger', type: 'button', onclick: () => r.remove() }, '✕')); r.get = () => ({ host: host.value, port: port.value, security: secu.value, login: login.value, password: pw.value }); smtpRows.append(r); };
    list(s.smtp).forEach(addSmtp); if (!list(s.smtp).length) addSmtp({});
    const contacts = h('textarea', { class: 'ltr' }, list(s.contacts).map((c) => c.email).join('\n'));
    const testTo = inp('', { class: 'ltr', placeholder: 'you@example.com' });
    return h('div', { class: 'stack' }, head(t('E-mails and alerts')), h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('Mail servers'), t('The first one that works sends'), smtpRows, h('div', {}, btn('+ ' + t('Mail server'), () => addSmtp({}), 'sm'))),
      fr(t('Sender'), null, h('div', { class: 'grid g2' }, sn, se)),
      fr(t('Contacts of the IT company'), t('One e-mail per line — they get the alerts of all customers'), contacts),
      fr(t('Test'), null, h('div', { class: 'inline' }, testTo, btn(t('Send a test e-mail'), async () => { const r = await api('POST', 'testmail', { to: testTo.value }); toast(r.sent === '1' ? t('Sent') : t('Not sent — see the Mail log'), r.sent !== '1'); }, 'sm'))))),
      savebar(async () => { const sm = Array.from(smtpRows.children).map((r) => r.get()); if (sm.some((x) => !x.host && (x.login || x.password || (x.port && !['25', '587', '465'].includes(String(x.port))) || (x.security && x.security !== 'STARTTLS')))) throw new Error(t('Write the address of the mail server.'));
        await api('POST', 'settings', { senderName: sn.value, senderEmail: se.value, smtpSet: 1, smtp: sm.filter((x) => x.host), contactsSet: 1, contacts: contacts.value.split(/\s+/).filter(Boolean).map((e) => ({ name: '', email: e })) }); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ security and sign-in
  PAGES.security = async () => {
    const [s, del, g] = await Promise.all([api('GET', 'settings'), api('GET', 'deletes'), api('GET', 'guard')]);
    // GUARD-010: addresses that guess passwords or scan the server are blocked
    const gOn = togRow(g.on === '1', t('Block an address that guesses passwords or scans the server'));
    const gFails = num(g.fails, { min: 3, max: 100 }), gUsers = num(g.users, { min: 2, max: 50 }), gProbes = num(g.probes, { min: 10, max: 1000 }), gHours = num(g.blockHours, { min: 1, max: 8760 });
    const gTrusted = h('textarea', { class: 'ltr', rows: 2, placeholder: '203.0.113.10, 198.51.100.0/24' }, g.trusted || '');
    const gNoCode = h('textarea', { class: 'ltr', rows: 2, placeholder: '203.0.113.10' }, g.noCode || '');
    const ipBox = inp('', { class: 'ltr', placeholder: '203.0.113.5' }), ipHours = num(24, { min: 1, max: 8760 });
    const blocked = list(g.blocked);
    const att = num(s.autoLock || 3, { min: 1, max: 10 }), mins = num(s.lockMinutes || 30, { min: 5 });
    const rf = num(s.ransomMinFiles, { min: 1 }), rp = num(s.ransomPercent, { min: 1, max: 100 });
    const dual = togRow(del.dual === '1', t('A second administrator approves every deletion'));
    return h('div', { class: 'stack' }, head(t('Security and sign-in')), h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('Two-step verification'), null, h('div', { class: 'note lock' }, '🔒', h('span', {}, t('Mandatory for every administrator — except from the fixed addresses below.')))),
      fr(t('Sign in without the code from'), t('Fixed addresses only (your office). The password is still needed. Anyone at that address can sign in with the password alone.'), gNoCode),
      fr(t('Lock after wrong passwords'), t('The lock cannot be switched off: at most 10 attempts, at least 5 minutes.'), h('div', { class: 'inline' }, att, h('span', {}, t('attempts, for')), mins, h('span', {}, t('minutes')))),
      fr(t('Password rule'), null, h('span', {}, t('At least 8 characters, with at least one letter'))),
      fr(t('Suspected ransomware'), t('Old versions are frozen when a backup changes more than this'), h('div', { class: 'inline' }, h('span', {}, t('at least')), rf, h('span', {}, t('files and')), rp, h('span', {}, '%'))),
      fr(t('Deleting'), t('A customer, a set or backups'), dual, h('span', { class: 'muted small' }, t('Even after approval the data stays 14 days in the recycle bin.'))),
      fr(t('Attacks from the internet'), t('Within {0} minutes from one address. The office network and this server are never blocked.', g.window), gOn,
        h('div', { class: 'inline' }, gFails, h('span', {}, t('wrong passwords, or')), gUsers, h('span', {}, t('different user names, or')), gProbes, h('span', {}, t('refused requests (scanning)'))),
        h('div', { class: 'inline' }, h('span', {}, t('block the address for')), gHours, h('span', {}, t('hours')))),
      fr(t('Never block'), t('Addresses or ranges, separated by commas — e.g. a branch office'), gTrusted))),
      savebar(async () => { await api('POST', 'settings', { autoLock: att.value, lockMinutes: mins.value, ransomMinFiles: rf.value, ransomPercent: rp.value }); await api('POST', 'deletes/settings', { dual: dual.input.checked ? 1 : 0 });
        await api('POST', 'guard', { on: gOn.input.checked ? 1 : 0, fails: gFails.value, users: gUsers.value, probes: gProbes.value, blockHours: gHours.value, trusted: gTrusted.value, noCode: gNoCode.value }); }, () => go({ page: 'dash' }))),
      card(t('Blocked addresses'), blocked.length ? pill('bad', String(blocked.length)) : pill('ok', t('None')),
        table([t('Address'), t('Reason'), t('Since'), t('Until'), ''], blocked.map((x) => [h('b', { class: 'ltr' }, x.ip), h('span', {}, x.reason, x.by && x.by !== 'automatic' ? h('span', { class: 'sub' }, t('By {0}', x.by)) : null), N(when(x.since)), N(when(x.until)),
          btn(t('Unblock'), async () => { await api('POST', 'guard/unblock', { ip: x.ip }); toast(t('Unblocked')); render(); }, 'sm')]), null, { empty: t('No address is blocked now.') }),
        h('div', { class: 'inline', style: 'margin-top:12px' }, ipBox, h('span', {}, t('for')), ipHours, h('span', {}, t('hours')),
          btn(t('Block'), async () => { if (!ipBox.value.trim()) throw new Error(t('Write an address.')); await api('POST', 'guard/block', { ip: ipBox.value.trim(), hours: ipHours.value }); toast(t('Blocked')); render(); }, 'sm danger'))),
      h('div', { class: 'note' }, 'ⓘ', h('span', {}, t('The administrators are in "Administrators".'), ' ', h('button', { class: 'btn link', type: 'button', onclick: () => go({ page: 'admins' }) }, t('Open')))));
  };

  // ------------------------------------------------------------------ clock and time zone (TIME-010)
  PAGES.time = async () => {
    const s = await api('GET', 'time');
    const country = sel([['', '—'], ...list(s.countries).map((c) => [c.code, c.code + ' · ' + c.zone])], s.country || ''), zone = inp(s.zone, { class: 'ltr' });
    country.addEventListener('change', () => { const c = list(s.countries).find((x) => x.code === country.value); if (c) zone.value = c.zone; });
    const ntp = inp(s.ntp, { class: 'ltr' }), ntpOn = togRow(s.ntpOn === '1', t('Take the time from this server'));
    return h('div', { class: 'stack' }, head(t('Clock and time zone'), t('Every time in the server, the logs, the e-mails and the reports follows this setting.')), h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
      fr(t('Country'), t('The time zone and daylight saving as kept in the country'), country), fr(t('Time zone'), null, h('div', { class: 'inline' }, zone, h('span', { class: 'muted' }, t('Now: {0}', s.now)))),
      fr(t('Time server (NTP)'), s.canSetNtp === '1' ? t('Windows: applied to this computer when saved') : t('Applied on Windows servers'), ntp, ntpOn))),
      savebar(async () => { await api('POST', 'time', { country: country.value, zone: zone.value, ntp: ntp.value, ntpOn: ntpOn.input.checked ? 1 : 0, apply: 1 }); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ integrations: AI, ITSguard export, client software, service calls outside
  async function downloadClient(os) {
    const r = await fetch('/api/admin/clientpackage' + (os ? '?os=' + os : ''), { headers: { 'X-Session': S.session } });
    if (!r.ok) { let m = t('Error {0}', r.status); try { m = tr(fromXml(new DOMParser().parseFromString(await r.text(), 'application/xml').documentElement).message) || m; } catch (e) { } throw new Error(m); }
    const name = ((r.headers.get('Content-Disposition') || '').match(/filename="([^"]+)"/) || [])[1] || 'Client-Setup.exe';
    const a = h('a', { href: URL.createObjectURL(await r.blob()), download: name }); document.body.append(a); a.click(); a.remove(); toast(t('Client software downloaded: {0}', name));
  }
  PAGES.integr = async () => {
    const s = await api('GET', 'settings');
    const aiOn = togRow(s.aiOn === '1', t('AI assistant on')), aiKey = h('input', { type: 'password', placeholder: s.aiHasKey === '1' ? t('(saved — empty = no change)') : 'sk-ant-…' }), aiModel = inp(s.aiModel, { class: 'ltr' });
    const aiAuto = togRow(s.aiAutoDiagnose === '1', t('Explain every failed backup by itself')), aiSearch = togRow(s.aiSearch === '1', t('Restore search in plain words'));
    const tUrl = inp(s.aiTicketUrl, { class: 'ltr', placeholder: 'https://helpdesk.example.com/api/tickets' }), tTok = h('input', { type: 'password', placeholder: s.aiHasTicketToken === '1' ? t('(saved — empty = no change)') : '' });
    const exP = inp(s.exportProfiles, { class: 'ltr' }), exL = inp(s.exportLogs, { class: 'ltr' });
    return h('div', { class: 'stack' }, head(t('Integrations'), t('Outside connections — none is required. The service calls are built into the system.')),
      card(t('Client software'), null, h('p', { class: 'muted' }, t('With your name, logo and the server address inside.')), h('div', { class: 'inline' }, btn('⬇ Windows', () => downloadClient(), 'pri'), btn('⬇ Linux', () => downloadClient('linux')), btn('⬇ Mac', () => downloadClient('mac')), btn('⬇ ZIP', () => downloadClient('zip')))),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        fr(t('AI assistant'), t('Explains failures and finds files in plain words. It reads only logs, never files.'), aiOn, aiAuto, aiSearch),
        fr(t('API key'), null, aiKey), fr(t('Model'), null, aiModel),
        fr(t('Outside service-call system'), t('Optional: a ticket is also sent there (HaloPSA, ConnectWise, Autotask, a webhook)'), tUrl, tTok),
        fr(t('Export to ITSguard'), t('Folders ITSguard reads reports from'), exP, exL))),
        savebar(async () => { await api('POST', 'settings', { aiOn: aiOn.input.checked ? 1 : 0, aiKey: aiKey.value || undefined, aiModel: aiModel.value, aiAutoDiagnose: aiAuto.input.checked ? 1 : 0, aiSearch: aiSearch.input.checked ? 1 : 0, aiTicketUrl: tUrl.value, aiTicketToken: tTok.value || undefined, exportProfiles: exP.value, exportLogs: exL.value }); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ client software (SETUP-C80): everything the customer's installation carries, on one page
  PAGES.client = async () => {
    const s = await api('GET', 'settings'), c = await api('GET', 'contract');
    const url = inp(s.publicUrl, { class: 'ltr', placeholder: 'https://backup.company.co.il:8443' }), pin = inp(s.certPin, { class: 'ltr' });
    const texts = {}; list(c.texts).forEach((x) => { texts[x.lang] = x.text; });
    const langs = Object.keys(I18N.names);
    const lang = sel(langs.map((l) => [l, I18N.names[l] + (texts[l] ? ' ✓' : '')]), I18N.lang in texts ? I18N.lang : (Object.keys(texts)[0] || I18N.lang));
    lang.dataset.view = '1';
    const area = h('textarea', { style: 'min-height:240px', placeholder: t('Paste your contract here. Empty: the standard license agreement is shown.') }, texts[lang.value] || '');
    lang.addEventListener('change', () => { area.value = texts[lang.value] || ''; });
    area.addEventListener('input', () => { texts[lang.value] = area.value; });
    // PKG-090 (the owner's client had the old address, one digit off from the real one): the address in the installation is checked
    // against the address this page is open on; a different one is shown in red with one button to take this one
    const here = location.origin, local = /^(localhost|127\.|\[?::1)/.test(location.hostname);
    const same = (a) => { try { const u = new URL(a); return u.host.toLowerCase() === location.host.toLowerCase(); } catch (e) { return false; } };
    const warn = h('div', { class: 'note bad addrwarn', hidden: local || same(s.publicUrl) },
      h('b', {}, t('Your customers will connect to {0}, but this page is open on {1}.', s.publicUrl || t('(no address)'), here)), ' ',
      btn(t('Use {0}', here), async () => { url.value = here; await api('POST', 'settings', { publicUrl: here, certPin: pin.value }); warn.hidden = true; toast(t('Saved. Download the installation again so it carries the changes.')); }, 'pri'));
    url.addEventListener('input', () => { warn.hidden = local || same(url.value); });
    const inst = togRow(c.install === '1', t('Show it in the installation — the customer accepts it to install'));
    const open = togRow(c.signupOpen === '1', t('New customers can open an account from the program'));
    return h('div', { class: 'stack' }, head(t('Client software'), t('What your customers install: your name and logo, your server address and your contract. Change it here, then download the installation.')),
      card(t('1. Download the installation'), null, h('p', { class: 'muted' }, t('One file: the customer double-clicks it — welcome, license agreement, installation, then the program opens to sign in.')),
        h('div', { class: 'inline' }, btn('⬇ Windows (Setup.exe)', () => downloadClient(), 'pri'), btn('⬇ Linux', () => downloadClient('linux')), btn('⬇ Mac', () => downloadClient('mac')), btn('⬇ ZIP', () => downloadClient('zip')))),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        h('h3', {}, t('2. The server the program connects to')),
        warn, fr(t('Server address for customers'), t('Filled in for the customer; it can still type another one'), url), fr(t('Certificate fingerprint (for a self-signed certificate)'), null, pin),
        h('h3', {}, t('3. Your contract (the license agreement of the installation)')),
        fr(t('Language'), t('The customer sees the contract in its language (else English)'), lang), fr(t('Text of the contract'), t('A changed text is a new version; every version is kept'), area),
        fr(t('Version'), null, h('span', {}, c.version && c.version !== '0' ? t('Version {0} from {1}', c.version, c.date) : t('No contract yet'))),
        fr(t('Where it is shown'), null, inst), fr(t('Sign-up'), null, open))),
        savebar(async () => {
          await api('POST', 'settings', { publicUrl: url.value, certPin: pin.value });
          await api('POST', 'contract', { install: inst.input.checked ? 1 : 0, signupOpen: open.input.checked ? 1 : 0, texts: Object.entries(texts).map(([l, x]) => ({ lang: l, text: x })) });
          toast(t('Saved. Download the installation again so it carries the changes.'));
        }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ contract and sign-up (CONTRACT-010, SIGNUP-010)
  PAGES.contract = async () => {
    const c = await api('GET', 'contract');
    const langs = Object.keys(I18N.names);
    const texts = {}; list(c.texts).forEach((x) => { texts[x.lang] = x.text; });
    const lang = sel(langs.map((l) => [l, I18N.names[l] + (texts[l] ? ' ✓' : '')]), I18N.lang in texts ? I18N.lang : (Object.keys(texts)[0] || I18N.lang));
    const area = h('textarea', { style: 'min-height:260px' }, texts[lang.value] || '');
    lang.dataset.view = '1';   // chooses which text is shown — not a setting
    lang.addEventListener('change', () => { area.value = texts[lang.value] || ''; });
    area.addEventListener('input', () => { texts[lang.value] = area.value; });
    const fl = (k, l) => togRow(c[k] === '1', l);
    const inst = fl('install', t('At the installation of the client software (must be accepted to go on)')), sup = fl('signup', t('At sign-up of a new customer')), re = fl('reaccept', t('Existing customers accept a new version at their next sign-in'));
    const open = fl('signupOpen', t('New customers can sign up from the client software'));
    return h('div', { class: 'stack' }, head(t('Contract and sign-up'), t('Your contract with your customers. The customer sees it when installing and when signing up, and accepts it before going on.')),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' },
        fr(t('Language'), t('The customer sees the contract in its language (else English)'), lang), fr(t('Text of the contract'), t('A changed text is a new version; every version is kept'), area),
        fr(t('Version'), null, h('span', {}, c.version && c.version !== '0' ? t('Version {0} from {1}', c.version, c.date) : t('No contract yet'))),
        fr(t('Where it is shown'), null, inst, sup, re), fr(t('Sign-up'), t('New customers are opened from the client software only'), open))),
        savebar(async () => { await api('POST', 'contract', { install: inst.input.checked ? 1 : 0, signup: sup.input.checked ? 1 : 0, reaccept: re.input.checked ? 1 : 0, signupOpen: open.input.checked ? 1 : 0, texts: Object.entries(texts).map(([l, x]) => ({ lang: l, text: x })) }); }, () => go({ page: 'dash' }))));
  };

  // ------------------------------------------------------------------ branding (BRAND-010)
  PAGES.brand = async () => {
    const s = S.vendor ? await api('GET', 'me') : await api('GET', 'settings');
    const val = (k) => S.vendor ? s['own' + k] : s['brand' + k];
    const b = BRAND.map(([k]) => brandInput(k, val(k))), logo = logoInput(val('LOGO'));
    const url = S.vendor ? null : inp(s.publicUrl, { class: 'ltr', placeholder: 'https://backup.company.co.il:8443' }), pin = S.vendor ? null : inp(s.certPin, { class: 'ltr' });
    return h('div', { class: 'stack' }, head(t('Branding'), t('What your customers see in the software, the reports and the e-mails. Your customers see no other name.')),
      h('section', { class: 'card' }, h('div', { class: 'cb' }, h('div', { class: 'form' }, BRAND.map(([, l], i) => fr(t(l), null, b[i])), fr(t('Logo'), t('PNG or JPEG, up to 200 KB'), logo),
        url ? fr(t('Server address for customers'), null, url) : null, pin ? fr(t('Certificate fingerprint (for a self-signed certificate)'), null, pin) : null)),
        savebar(async () => { const body = { brandLOGO: logo.value }; BRAND.forEach(([k], i) => { body['brand' + k] = b[i].value; }); if (url) { body.publicUrl = url.value; body.certPin = pin.value; } await api('POST', S.vendor ? 'brand' : 'settings', body); const me = await api('GET', 'me'); brand(me); }, () => go({ page: 'dash' }))),
      card(t('Client software'), null, h('div', { class: 'inline' }, btn('⬇ Windows', () => downloadClient(), 'pri'), btn('⬇ Linux', () => downloadClient('linux')), btn('⬇ Mac', () => downloadClient('mac')), btn('⬇ ZIP', () => downloadClient('zip')))));
  };

  // ------------------------------------------------------------------ start
  I18N.onChange && I18N.onChange(() => { if (S.me) render(); else loginPage(); });
  fetch('/api/brand').then((r) => r.text()).then((x) => { try { brand(fromXml(new DOMParser().parseFromString(x, 'application/xml').documentElement)); } catch (e) { } if (!S.session) loginPage(); });
  if (S.session) start(); else loginPage();
})();
