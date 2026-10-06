'use strict';
// PORTAL-010: the partner portal. An IT company signs up, describes its product, downloads its own server (the wizard is
// filled in and the licence arrives by itself) and its client software for Windows, Mac and Linux. The owner signs in as "owner".
(async function () {
  await I18N.load(I18N.pick('en'));
  document.getElementById('langbox').append(I18N.selector());
  document.querySelectorAll('[data-t]').forEach((e) => { e.textContent = t(e.getAttribute('data-t')); });
  const $ = (s) => document.querySelector(s);
  const main = $('#main');
  const S = { session: sessionStorage.getItem('obPortal'), owner: sessionStorage.getItem('obPortalOwner') === '1', tab: 'register', info: {} };

  function h(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (k === 'class') e.className = v;
      else if (k === 'style') e.style.cssText = v;   // CSSOM: allowed by the page's Content-Security-Policy (a style attribute is not)
      else if (k === 'value') e.value = v;
      else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids.flat(Infinity)) if (k !== null && k !== undefined && k !== false) e.append(k instanceof Node ? k : String(k));
    return e;
  }
  async function api(method, op, body, raw) {
    const r = await fetch('/portal/api/' + op, { method, headers: Object.assign({ 'Content-Type': 'application/json' }, S.session ? { 'X-Session': S.session } : {}), body: body ? JSON.stringify(body) : undefined });
    if (raw && r.ok) return r;
    let j = {}; try { j = await r.json(); } catch (e) { }
    if (r.status === 401 && op !== 'login') { signOut(); throw new Error(tr(j.message) || t('Please sign in again.')); }
    if (!r.ok) throw new Error(tr(j.message) || t('Error {0}', r.status));
    return j;
  }
  const put = (el, ...kids) => el.replaceChildren(...kids.flat(Infinity).filter((k) => k !== null && k !== undefined && k !== false));
  function toast(m, err) { const x = $('#toast'); x.textContent = m; x.className = err ? 'err' : ''; x.hidden = false; clearTimeout(toast.t); toast.t = setTimeout(() => { x.hidden = true; }, err ? 7000 : 3500); }
  const wrap = (fn) => async (ev) => { if (ev && ev.preventDefault) ev.preventDefault(); try { await fn(ev); } catch (e) { toast(e.message, true); } };
  async function save(r, fallback) {
    const name = ((r.headers.get('Content-Disposition') || '').match(/filename="([^"]+)"/) || [])[1] || fallback;
    const a = h('a', { href: URL.createObjectURL(await r.blob()), download: name }); document.body.append(a); a.click(); a.remove();
    toast(t('Downloaded: {0}', name));
  }
  function signOut() { S.session = null; sessionStorage.removeItem('obPortal'); sessionStorage.removeItem('obPortalOwner'); $('#logout').hidden = true; $('#who').textContent = ''; welcome(); }
  $('#logout').addEventListener('click', signOut);
  function signedIn(r) { S.session = r.session; S.owner = !!r.owner; sessionStorage.setItem('obPortal', r.session); sessionStorage.setItem('obPortalOwner', r.owner ? '1' : '0'); start(); }

  // ---- welcome: sign up / sign in ----
  function welcome() {
    const inp = (a) => h('input', a);
    const f = { company: inp({ autocomplete: 'organization', required: true }), contact: inp({ autocomplete: 'name', required: true }), email: inp({ type: 'email', dir: 'ltr', autocomplete: 'email', required: true }),
      phone: inp({ type: 'tel', dir: 'ltr', autocomplete: 'tel' }), country: inp({ autocomplete: 'country-name' }), password: inp({ type: 'password', autocomplete: 'new-password', required: true }) };
    const terms = h('input', { type: 'checkbox', id: 'terms' });
    const le = inp({ type: 'text', inputmode: 'email', dir: 'ltr', autocomplete: 'username', required: true }), lp = inp({ type: 'password', autocomplete: 'current-password', required: true });
    const reg = h('form', { onsubmit: wrap(async () => {
      const b = { terms: terms.checked ? '1' : '0', lang: I18N.lang }; for (const [k, e] of Object.entries(f)) b[k] = e.value;
      signedIn(await api('POST', 'register', b)); toast(t('Welcome! Your account is ready.'));
    }) }, h('label', {}, t('Company name')), f.company, h('label', {}, t('Your name')), f.contact,
      h('div', { class: 'grid2' }, h('div', {}, h('label', {}, t('E-mail')), f.email), h('div', {}, h('label', {}, t('Phone')), f.phone)),
      h('label', {}, t('Country')), f.country, h('label', {}, t('Password')), f.password, h('div', { class: 'hint' }, t('At least 8 characters, with at least one letter.')),
      h('label', { class: 'check', for: 'terms' }, terms, t('I accept the terms of use and the privacy policy.')),
      h('div', { class: 'bar' }, h('button', { class: 'primary' }, t('Create my account'))));
    const login = h('form', { onsubmit: wrap(async () => signedIn(await api('POST', 'login', { email: le.value, password: lp.value }))) },
      h('label', {}, t('E-mail')), le, h('label', {}, t('Password')), lp, h('div', { class: 'bar' }, h('button', { class: 'primary' }, t('Sign in'))));
    const tabs = h('div', { class: 'tabs' }, h('button', { type: 'button', class: S.tab === 'register' ? 'on' : '', onclick: () => { S.tab = 'register'; welcome(); } }, t('New partner')),
      h('button', { type: 'button', class: S.tab === 'login' ? 'on' : '', onclick: () => { S.tab = 'login'; welcome(); } }, t('Sign in')));
    put(main,
      h('div', { class: 'hero' }, h('div', { class: 'in' },
        h('div', {}, h('p', { class: 'eyebrow' }, h('span', { class: 'ai-badge' }, t('AI-powered backup'))),
          h('h1', {}, t('Backup that thinks.'), ' ', h('span', { class: 'ai-text' }, t('Under your brand.'))),
          h('p', {}, t('Sign up, enter your company details and logo, and download a backup server and client software that carry your name. Your customers never see ours.')),
          h('div', { class: 'stats' }, h('span', {}, h('b', {}, '✓ '), S.info.trialDays > 0 ? t('{0}-day free trial', S.info.trialDays) : t('Free up to {0} computers and {1} GB', 10, 500)), h('span', {}, h('b', {}, '✓ '), t('13 languages')), h('span', {}, h('b', {}, '✓ '), t('Encrypted on the customer\'s computer')))),
        // DESIGN-050: what the AI does, shown — not told (example events)
        h('div', { class: 'console', 'aria-hidden': 'true' }, h('div', { class: 'top' }, h('span', { class: 'live' }), t('AI assistant — live'), h('span', { class: 'ai-badge' }, 'AI')),
          [['🛡', t('Unusual change on ACCOUNTING-PC — 4,812 files renamed'), t('Old versions frozen. Ransomware suspected.')],
           ['✦', t('Backup failed on SQL-SERVER: the disk is full'), t('Fix: free 12 GB on drive D:. A service ticket was opened.')],
           ['↗', t('The backup disk will be full in 41 days'), t('Plan more space this month.')]].map(([i, a, b]) => h('div', { class: 'ev' }, h('i', {}, i), h('div', {}, a, h('small', {}, b))))))),
      h('div', { class: 'features' }, [['🖥', t('Windows, Mac and Linux computers and servers')], ['☁', t('Microsoft 365, Google Workspace, SQL, Exchange, VMware, Hyper-V')], ['🔒', t('Encrypted on the customer\'s computer')], ['✦', t('AI assistant, ransomware detection, restore from the web'), 'ai']]
        .map(([i, x, c]) => h('div', { class: c || '' }, h('i', { 'aria-hidden': 'true' }, i), h('span', {}, x)))),
      S.info.https === false && location.hostname !== 'localhost' && location.hostname !== '127.0.0.1' ? h('div', { class: 'warn' }, t('This page is not on a secure (https) address. Do not use a password you use elsewhere.')) : null,
      h('div', { class: 'card auth' }, tabs, S.tab === 'register' && S.info.signupOpen !== false ? reg : S.tab === 'register' ? h('p', {}, t('New sign-ups are closed. Contact the software vendor.')) : login));
  }

  // ---- the partner's pages ----
  const BRAND = [['PRODUCT', 'Product name'], ['SLOGAN', 'Slogan (optional)'], ['COMPANY', 'Company name'], ['PHONE', 'Support phone'], ['EMAIL', 'Support e-mail'], ['WEBSITE', 'Website']];
  async function partner() {
    const me = await api('GET', 'me');
    $('#who').textContent = me.company + ' · ' + me.email;
    const b = me.brand || {};
    const f = {}; for (const [k] of BRAND) f[k] = h('input', { value: b[k] || '', dir: k === 'PHONE' || k === 'EMAIL' || k === 'WEBSITE' ? 'ltr' : 'auto' });
    const hex = (v, d) => /^#[0-9a-fA-F]{6}$/.test(v || '') ? v : d;
    const color = h('input', { type: 'color', value: hex(b.COLOR, '#4F46E5') }), accent = h('input', { type: 'color', value: hex(b.ACCENT, '#F97316') });
    // DESIGN-020: colour pairs chosen for selling, not only for taste — each with the reason
    const PAIRS = [['#4F46E5', '#F97316', 'Innovation + action', 'Indigo says "advanced technology you can trust" — it matches the AI; orange buttons stand out on it, so customers see the next step and click.', true],
      ['#1D4ED8', '#F97316', 'Trust + action', 'Blue says "safe and reliable"; orange buttons stand out on it, so customers see the next step and click. The most common pair in security software.'],
      ['#0F3D3E', '#10B981', 'Protection + growth', 'Dark teal and green say "everything is protected and fine" — calm, for IT companies that sell peace of mind.'],
      ['#1F2937', '#EAB308', 'Premium', 'Charcoal with gold feels premium and established — for law firms, accountants and clinics.']];
    const pairs = h('div', { class: 'palettes' });
    const drawPairs = () => put(pairs, PAIRS.map(([c1, c2, n, why, rec]) => h('button', { type: 'button', class: color.value.toLowerCase() === c1.toLowerCase() && accent.value.toLowerCase() === c2.toLowerCase() ? 'on' : '', onclick: () => { color.value = c1; accent.value = c2; preview(); } },
      h('span', { class: 'dots' }, h('i', { style: 'background:' + c1 }), h('i', { style: 'background:' + c2 })), h('b', {}, t(n)), rec ? h('span', { class: 'rec' }, t('Recommended')) : null, h('small', {}, t(why)))));
    const lang = h('select', {}, Object.entries(I18N.names).map(([c, n]) => h('option', { value: c, selected: c === (b.LANGUAGE || 'en') }, n)));
    let logo = b.LOGO || '';
    const logoFile = h('input', { type: 'file', accept: 'image/png,image/jpeg' });
    const pv = { top: h('div', { class: 'top' }), body: h('div', { class: 'body' }), cta: h('span', { class: 'cta' }, t('Back up now')) };
    const preview = () => {
      pv.top.style.background = 'linear-gradient(110deg, #0B1A33 0%, ' + color.value + ' 100%)'; pv.top.style.borderBottomColor = accent.value; pv.cta.style.background = accent.value; drawPairs();
      put(pv.top, logo ? h('img', { src: logo, alt: '' }) : null, h('div', {}, h('b', {}, f.PRODUCT.value || t('Product name')), f.SLOGAN.value ? h('small', {}, f.SLOGAN.value) : null));
      put(pv.body, pv.cta, h('span', {}, [f.COMPANY.value, f.PHONE.value, f.EMAIL.value, f.WEBSITE.value].filter(Boolean).join(' · ')));
    };
    Object.values(f).forEach((e) => e.addEventListener('input', preview)); color.addEventListener('input', preview); accent.addEventListener('input', preview);
    logoFile.onchange = () => { const x = logoFile.files[0]; if (!x) return; if (x.size > 300000) { toast(t('The logo must be PNG or JPEG, up to 300 KB.'), true); logoFile.value = ''; return; } const r = new FileReader(); r.onload = () => { logo = r.result; preview(); }; r.readAsDataURL(x); };
    preview();
    const saveBrand = async () => { const body = { COLOR: color.value, ACCENT: accent.value, LANGUAGE: lang.value, LOGO: logo }; for (const [k] of BRAND) body[k] = f[k].value; await api('POST', 'brand', body); };

    const servers = me.servers || [];
    const serverId = h('input', { dir: 'ltr', placeholder: 'OB-…' });
    const licOut = h('textarea', { class: 'mono', readonly: true, hidden: !servers.length });
    if (servers.length) licOut.value = servers[servers.length - 1].license || '';
    const url = h('input', { dir: 'ltr', placeholder: 'https://backup.mycompany.com:8443' }), pin = h('input', { dir: 'ltr', placeholder: t('SHA-256 (shown by the installer)') });
    const client = (os) => wrap(async () => { await saveBrand(); toast(t('Preparing the files…')); await save(await api('POST', 'download/client', { os, serverUrl: url.value, pin: pin.value }, true), 'Client-Setup'); });

    put(main,
      h('div', { class: 'card' }, h('h2', {}, h('span', { class: 'step' }, '1'), t('Your product')), h('p', { class: 'lead' }, t('This is what your customers will see in the software, the reports and the e-mails.')),
        h('div', { class: 'grid2' }, BRAND.map(([k, l]) => h('div', {}, h('label', {}, t(l)), f[k]))),
        h('label', {}, t('Colours')), h('p', { class: 'hint' }, t('Two colours, each with a job: the brand colour builds trust, the action colour leads to the next step. Pick a pair or choose your own.')), pairs,
        h('div', { class: 'grid2' }, h('div', {}, h('label', {}, t('Brand color')), color), h('div', {}, h('label', {}, t('Action color (buttons)')), accent)),
        h('label', {}, t('Language of e-mails and client software')), lang,
        h('label', {}, t('Logo (PNG or JPEG, optional)')), logoFile,
        h('div', { class: 'preview' }, pv.top, pv.body),
        h('div', { class: 'bar' }, h('button', { class: 'primary', onclick: wrap(async () => { await saveBrand(); toast(t('Saved')); }) }, t('Save')))),
      h('div', { class: 'card' }, h('h2', {}, h('span', { class: 'step' }, '2'), t('Your backup server')),
        h('p', { class: 'lead' }, t('A Windows server or PC that is always on (Windows 10 / Server 2016 or later), with enough disk space for your customers\' backups.')),
        h('ol', {}, h('li', {}, t('Download the server and unzip it on that computer.')), h('li', {}, t('Double-click Setup.cmd — the wizard already has your product details.')),
          h('li', {}, S.info.trialDays > 0 ? t('Your trial licence is added automatically during the installation.') : t('The server starts in the free edition: up to 10 computers and 500 GB — no licence needed.'))),
        h('div', { class: 'bar' }, h('button', { class: 'primary', onclick: wrap(async () => { await saveBrand(); toast(t('Preparing the files…')); await save(await api('GET', 'download/server', null, true), 'Server.zip'); }) }, t('⬇ Download your server (Windows)')))),
      h('div', { class: 'card' }, h('h2', {}, h('span', { class: 'step' }, '3'), t('Licence')),
        servers.length ? h('div', { class: 'wide' }, h('table', {}, h('thead', {}, h('tr', {}, [t('Server ID'), t('Edition'), t('Valid until')].map((x) => h('th', {}, x)))),
          h('tbody', {}, servers.map((s) => h('tr', {}, h('td', { class: 'mono' }, s.serverId), h('td', {}, s.edition), h('td', { class: 'mono' }, s.expires))))))
          : h('p', { class: 'lead' }, S.info.trialDays > 0 ? t('Only if the licence was not added during the installation: copy the server ID from the management website (Company and product → Licence) here.')
            : t('Free edition: up to 10 computers and 500 GB, no licence needed.')),
        S.info.trialDays > 0 ? [h('label', {}, t('Server ID')), serverId,
          h('div', { class: 'bar' }, h('button', { onclick: wrap(async () => { const r = await api('POST', 'license', { serverId: serverId.value }); licOut.value = r.license; licOut.hidden = false; toast(t('Licence ready — paste it in the management website.')); }) }, t('Get licence')))] : null,
        licOut, h('p', { class: 'hint' }, t('For more customers, computers or storage, contact us for a full licence.'))),
      h('div', { class: 'card' }, h('h2', {}, h('span', { class: 'step' }, '4'), t('Client software for your customers')),
        h('p', { class: 'lead' }, t('After the server is installed: its address and certificate fingerprint are shown at the end of the installation. The same downloads are also in your management website.')),
        h('div', { class: 'grid2' }, h('div', {}, h('label', {}, t('Server address for customers')), url), h('div', {}, h('label', {}, t('Certificate fingerprint (for a self-signed certificate)')), pin)),
        h('div', { class: 'bar' }, h('button', { class: 'primary', onclick: client('windows') }, t('⬇ Windows')), h('button', { onclick: client('mac') }, t('⬇ Mac')), h('button', { onclick: client('linux') }, t('⬇ Linux')))));
  }

  // ---- the owner's page ----
  async function owner() {
    const r = await api('GET', 'owner/accounts');
    $('#who').textContent = t('Owner');
    const st = r.settings || {};
    const num = (v) => h('input', { type: 'number', min: '0', value: String(v ?? '') });
    const s = { trialDays: num(st.trialDays), trialUsers: num(st.trialUsers), trialComputers: num(st.trialComputers), trialStorageGB: num(st.trialStorageGB) };
    const open = h('input', { type: 'checkbox' }); open.checked = st.signupOpen !== false;
    const licDialog = (a) => {
      const f = { serverId: h('input', { dir: 'ltr', value: ((a.servers || [])[0] || {}).serverId || '' }), edition: h('select', {}, ['PRO', 'BUSINESS', 'TRIAL'].map((x) => h('option', { value: x }, x))),
        users: num(100), computers: num(500), storageGB: num(5000), days: num(365) };
      const out = h('textarea', { class: 'mono', readonly: true, hidden: true });
      const d = h('dialog', {}, h('h3', {}, t('Licence — {0}', a.company)), h('div', { class: 'grid2' }, Object.entries({ serverId: 'Server ID', edition: 'Edition', users: 'Customers', computers: 'Computers', storageGB: 'Storage (GB)', days: 'Days' }).map(([k, l]) => h('div', {}, h('label', {}, t(l)), f[k]))),
        out, h('div', { class: 'bar' }, h('button', { class: 'primary', onclick: wrap(async () => { const b = {}; for (const [k, e] of Object.entries(f)) b[k] = e.value; b.id = a.id; const x = await api('POST', 'owner/account', b); out.value = x.license; out.hidden = false; toast(t('Licence issued')); }) }, t('Issue licence')),
          h('button', { onclick: () => { d.close(); d.remove(); owner(); } }, t('Close'))));
      document.body.append(d); d.showModal();
    };
    put(main,
      h('div', { class: 'card' }, h('h2', {}, t('Partners')), h('div', { class: 'wide' }, h('table', {}, h('thead', {}, h('tr', {}, [t('Company'), t('Contact'), t('Product'), t('Servers'), t('Status'), ''].map((x) => h('th', {}, x)))),
        h('tbody', {}, (r.accounts || []).map((a) => h('tr', {}, h('td', {}, a.company, h('div', { class: 'muted' }, (a.created || '').slice(0, 10))), h('td', {}, a.contact, h('div', { class: 'mono' }, a.email), h('div', { class: 'mono' }, a.phone || '')),
          h('td', {}, (a.brand || {}).PRODUCT || ''), h('td', {}, (a.servers || []).map((s) => h('div', { class: 'mono' }, s.serverId + ' · ' + s.edition + ' · ' + s.expires))),
          h('td', {}, a.status === 'active' ? h('span', { class: 'ok' }, t('Active')) : h('span', { class: 'bad' }, t('Disabled')), a.whiteLabel === '1' ? h('div', { class: 'muted' }, t('White label')) : null),
          h('td', {}, h('button', { class: 'link', onclick: () => licDialog(a) }, t('Licence')),
            h('button', { class: 'link', onclick: wrap(async () => { await api('POST', 'owner/account', { id: a.id, whiteLabel: a.whiteLabel === '1' ? '0' : '1' }); owner(); }) }, a.whiteLabel === '1' ? t('Show "Powered by"') : t('White label')),
            h('button', { class: 'link', onclick: wrap(async () => { await api('POST', 'owner/account', { id: a.id, status: a.status === 'active' ? 'disabled' : 'active' }); owner(); }) }, a.status === 'active' ? t('Disable') : t('Enable'))))))))),
      h('div', { class: 'card' }, h('h2', {}, t('Sign-up and trial')),
        h('label', { class: 'check' }, open, t('New partners can sign up')), h('p', { class: 'hint' }, t('Trial days: 0 = no trial — partners start on the free edition.')),
        h('div', { class: 'grid2' }, Object.entries({ trialDays: 'Trial days', trialUsers: 'Trial customers', trialComputers: 'Trial computers', trialStorageGB: 'Trial storage (GB)' }).map(([k, l]) => h('div', {}, h('label', {}, t(l)), s[k]))),
        h('div', { class: 'bar' }, h('button', { class: 'primary', onclick: wrap(async () => { const b = { signupOpen: open.checked ? '1' : '0' }; for (const [k, e] of Object.entries(s)) b[k] = Number(e.value); await api('POST', 'owner/settings', b); toast(t('Saved')); }) }, t('Save'))),
        h('p', { class: 'hint mono' }, st.package || t('No server package set'))));
  }

  async function start() {
    $('#logout').hidden = false;
    try { if (S.owner) await owner(); else await partner(); } catch (e) { toast(e.message, true); }
  }

  try { S.info = await (await fetch('/portal/api/info')).json(); } catch (e) { }
  if (S.session) start(); else welcome();
})();
