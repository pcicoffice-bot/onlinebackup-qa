'use strict';
// SETUP-001: the installation wizard. Plain questions; every text in the user's language (T below).
(function () {
  const KEY = location.hash.slice(1);
  const T = {
    en: {
      dir: 'ltr', title: 'Backup Server Setup',
      steps: ['Welcome', 'Your company', 'Storage', 'Address', 'Administrator', 'E-mail', 'Install'],
      next: 'Next', back: 'Back', exit: 'Exit without installing', install: 'Install now', update: 'Update now', closed: 'Setup was closed. You can close this tab.',
      welcome: 'Welcome', welcomeLead: 'This wizard installs the backup server on this computer. It takes about 5 minutes. You only need to answer a few simple questions — no IT knowledge required.',
      isServer: 'Windows Server — suitable', notServer: 'This is not Windows Server. The backup server is installed on Windows Server only (2016 or later): Windows 10 and 11 allow only 20 connections at a time, and customers would fail to back up.',
      ERR_NOT_SERVER: 'Install on Windows Server (2016 or later). Windows 10 and 11 are not supported.',
      storageAuto: 'The backups will be kept in {0} — the drive with the most free space ({1} GB free). More drives can be added later in the admin site.',
      checks: 'Checks', isAdmin: 'Running with administrator rights', notAdmin: 'Not running as administrator — close this window and run Setup.cmd again (it asks for permission).',
      portOk: 'Port 8443 is free', portBusy: 'Port 8443 is already used by another program — you will choose another one later.',
      existing: 'The backup server is already installed here ({0}). Continuing will UPDATE it: the program files are replaced, your data and settings are kept.',
      company: 'Your company', companyLead: 'Your customers will see your product name and your company details — not ours.',
      product: 'Product name', productHint: 'The name your customers see, e.g. "Acme Cloud Backup".', slogan: 'Slogan (optional)', sloganHint: 'A short line under the product name, e.g. "Your data, always safe".', companyName: 'Company name', phone: 'Support phone', email: 'Support e-mail', website: 'Website',
      color: 'Brand color', accent: 'Action color (buttons)', logo: 'Logo (PNG or JPEG, optional)',
      storage: 'Where to keep the backups', storageLead: 'Choose the drive with the most free space. A folder named "OnlineBackup" is created on it.',
      free: '{0} GB free of {1} GB', system: 'Windows drive', recommended: 'Recommended',
      folder: 'Folder for the backups', folderHint: 'Any folder on a local drive — it is created if it does not exist. More places can be added later in the management website.',
      address: 'How customers reach this server', addressLead: 'Your customers\' computers connect to this address over the internet.',
      hostChoice: 'Server address', useIp: 'Use the internet address of this office: {0}', useDomain: 'I have a domain name (recommended), for example backup.mycompany.com', noIp: 'The internet address could not be detected — enter a domain name or your public IP address.',
      host: 'Domain name or IP address', port: 'Port', portHint: 'Leave 8443 unless another program uses it.',
      router: 'One thing to do in your router (once):', routerText: 'Forward TCP port <b>{0}</b> to this computer: <b>{1}</b>. In the router\'s settings this is usually called "Port forwarding" or "Virtual server". Your internet provider or IT person can do it in 2 minutes.',
      admin: 'Administrator', adminLead: 'This is the sign-in for your management website. Keep the password safe.',
      login: 'User name', password: 'Password', password2: 'Password again', pwHint: 'At least 8 characters, with at least one letter.', pwMismatch: 'The two passwords are not the same.',
      alert: 'E-mail for alerts', alertHint: 'Where to send a warning when a customer\'s backup fails (optional).',
      mail: 'Sending e-mails', mailLead: 'So the server can send backup reports and alerts. You can skip this and set it later.',
      mailNone: 'Not now', mailGmail: 'Gmail / Google Workspace', mailM365: 'Microsoft 365 / Outlook', mailOther: 'Other mail server',
      smtpLogin: 'Sending e-mail address', smtpPassword: 'Its password', gmailHint: 'Gmail: use an "App password" (Google account → Security → App passwords), not your normal password.',
      m365Hint: 'Microsoft 365: the mailbox must allow "Authenticated SMTP" (Microsoft 365 admin → user → Mail → Manage email apps).', smtpHost: 'Mail server (SMTP)', smtpPort: 'Port',
      summary: 'Ready to install', summaryLead: 'Please check the details. Nothing is changed on this computer until you click "Install now".',
      sProduct: 'Product', sStorage: 'Backups folder', sAddress: 'Address for customers', sAdmin: 'Administrator', sMail: 'E-mails', none: 'not set',
      willDo: 'What the installer will do', willList: ['Copy the program files', 'Create the backup folders (only administrators can open them)', 'Create a security certificate for this server', 'Open port {0} in the Windows firewall', 'Install and start the Windows service "OnlineBackupServer"', 'Check that the server answers'],
      noIis: 'Your existing websites (IIS) and other programs are not touched.',
      installing: 'Installing…', updating: 'Updating…', doneTitle: 'Installation complete 🎉', updatedTitle: 'Update complete 🎉', failed: 'The installation stopped',
      failedHint: 'Nothing is lost. Fix the problem and click "Install now" again.',
      nextSteps: 'Next steps', open: 'Open the management website', step1: 'Sign in with your administrator user name and password.', step2: 'Go to "Settings" → "Client software": download it and send it to your customers.',
      step3: 'Make sure port {0} is forwarded in your router (see above).', pinLabel: 'Security fingerprint (already built into your client software)', print: 'Print these details', finish: 'Finish',
      certWarn: 'The browser will warn about the certificate the first time — this is expected for a private server. Choose "Advanced" → "Continue".',
      ERR_PRODUCT: 'Please enter the product name.', ERR_COMPANY: 'Please enter the company name.', ERR_HOST: 'The server address is not valid (a domain like backup.mycompany.com, or an IP address).',
      ERR_PORT_RANGE: 'The port must be between 1024 and 65535 (8443 recommended).', ERR_PORT_BUSY: 'Port {0} is used by another program on this computer. Choose another one, e.g. 9443.',
      ERR_DRIVE: 'Please choose a drive for the backups.', ERR_FOLDER: 'Write a full folder path on a local drive, e.g. E:\\OnlineBackup — not a folder of Windows or of programs, not a network path.', ERR_LOGIN: 'User name: 3–32 English letters or digits.', ERR_PASSWORD: 'Password: at least 8 characters, with at least one letter.',
      ERR_ALERT_EMAIL: 'The alert e-mail address is not valid.', ERR_SMTP_LOGIN: 'For e-mails: fill in the sending address and its password.', ERR_SMTP_HOST: 'For e-mails: fill in the mail server.',
      ERR_COLOR: 'The color is not valid.', ERR_LOGO: 'The logo must be PNG or JPEG, up to 300 KB.', ERR_NO_ANSWER: 'The server did not answer after the installation. Check the "OnlineBackupServer" service in Windows Event Viewer.',
      STEP_STOP: 'Stopping the existing service', STEP_COPY: 'Copying the program files', STEP_RESTART: 'Starting the service again', STEP_UPDATED: 'Update finished — data and settings kept',
      STEP_FOLDERS: 'Creating the backup folders in {0}', STEP_BRAND: 'Saving your company and product details', STEP_MAIL: 'E-mail sending through {0}', STEP_CERT: 'Creating the security certificate',
      STEP_HTTPS: 'Setting up the secure connection on port {0}', STEP_FIREWALL: 'Opening port {0} in the Windows firewall', STEP_SERVICE: 'Installing and starting the Windows service',
      STEP_LICENSE: 'Licence received from the partner portal (valid until {0})', STEP_LICENSE_LATER: 'The licence will be added later in the management website ({0})', STEP_LICENSE_FREE: 'Free edition: up to 10 computers and 500 GB, no licence needed.', STEP_VERIFY: 'Checking that the server answers', STEP_VERIFIED: 'The server answers on port {0}', STEP_DONE: 'Done'
    }
  };
  // I18N-010: English here; every other language from the shared dictionaries (/i18n/<lang>.json)
  const t = (k, ...a) => I18N.t(T.en[k] ?? k, ...a);
  const tl = (k) => T.en[k].map((x) => I18N.t(x));
  const msg = (code) => { const [k, arg] = String(code).split('|'); return t(k, arg); };

  const A = { language: '', product: '', slogan: '', company: '', phone: '', email: '', website: '', color: '#4F46E5', accent: '#F97316', logo: '', hostName: '', port: 8443, dataRoot: '', adminLogin: 'admin', adminPassword: '', password2: '',
    alertEmail: '', mailProvider: 'none', smtpHost: '', smtpPort: 587, smtpLogin: '', smtpPassword: '', hostMode: 'ip' };
  let INFO = null, step = 0, errors = [];
  const STEP_ERRORS = [['ERR_NOT_SERVER'], ['ERR_PRODUCT', 'ERR_COMPANY', 'ERR_COLOR', 'ERR_LOGO'], ['ERR_DRIVE', 'ERR_FOLDER'], ['ERR_HOST', 'ERR_PORT_RANGE', 'ERR_PORT_BUSY'], ['ERR_LOGIN', 'ERR_PASSWORD', 'ERR_ALERT_EMAIL'], ['ERR_SMTP_LOGIN', 'ERR_SMTP_HOST'], []];

  function h(tag, attrs, ...kids) {
    const e = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs || {})) {
      if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
      else if (k === 'class') e.className = v;
      else if (k === 'html') e.innerHTML = v;
      else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? '' : v);
    }
    for (const k of kids.flat(Infinity)) if (k !== null && k !== undefined && k !== false) e.append(k instanceof Node ? k : String(k));
    return e;
  }
  const esc = (s) => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  // the wizard's own small server stops when told — its answer may never arrive, so the page goes on either way
  // (before, "Finish" waited for that answer and nothing happened); Finish opens the management website on this server
  async function closeSetup(then) {
    try { await Promise.race([call('close', {}), new Promise(res => setTimeout(res, 1500))]); } catch (e) { }
    if (then) { location.href = then; return; }
    document.body.innerHTML = '<main><div class="card">' + esc(t('closed')) + '</div></main>';
  }
  async function call(path, body) {
    const r = await fetch('/api/' + path, { method: body ? 'POST' : 'GET', headers: { 'X-Key': KEY, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
    return r.json();
  }
  function field(key, label, attrs, hint) {
    const i = h('input', Object.assign({ value: A[key] ?? '', oninput: () => { A[key] = i.value; } }, attrs || {}));
    return [h('label', { for: key }, label), Object.assign(i, { id: key }), hint ? h('div', { class: 'hint' }, hint) : null];
  }
  const errBox = () => errors.length ? h('div', { class: 'errors', role: 'alert' }, errors.map(e => h('div', {}, msg(e)))) : null;

  function nav(extra) {
    return h('div', { class: 'nav' },
      step > 0 && step < 6 ? h('button', { onclick: () => { errors = []; step--; render(); } }, t('back')) : null,
      h('button', { class: 'link', onclick: () => closeSetup() }, t('exit')),
      h('span', { class: 'space' }), extra);
  }
  async function next() {
    if (step === 4 && A.adminPassword !== A.password2) { errors = ['pwMismatch']; render(); return; }
    if (step === 3 && A.hostMode === 'ip') A.hostName = INFO.publicIp || A.hostName;
    const r = await call('check', A);
    errors = (r.errors || []).filter(e => STEP_ERRORS[step].includes(e.split('|')[0]));
    if (!errors.length) step = INFO.existing && step === 0 ? 6 : step + 1;
    render();
  }

  function render() {
    I18N.apply(); document.title = A.product ? A.product + ' — ' + t('title') : t('title');
    // BRAND-010: the company's brand from the first screen — its name, slogan, logo and colours
    const ti = document.getElementById('title'); ti.replaceChildren(A.product || t('title'), A.product ? h('small', {}, A.slogan || t('title')) : '');
    const lg = document.getElementById('hlogo'); if (A.logo && /^data:image\/(png|jpeg);base64,/.test(A.logo)) { lg.src = A.logo; lg.hidden = false; } else lg.hidden = true;
    for (const [v, c] of [['--brand', A.color], ['--accent', A.accent]]) if (/^#[0-9a-fA-F]{6}$/.test(c || '')) document.documentElement.style.setProperty(v, c);
    const steps = document.getElementById('steps'); steps.replaceChildren(...tl('steps').map((s, i) => h('span', { class: i === step ? 'on' : i < step ? 'done' : '' }, (i + 1) + '. ' + s)));
    const card = document.getElementById('card');
    const P = [welcome, company, storage, address, admin, mail, summary][step];
    card.replaceChildren(...P().flat(Infinity).filter(Boolean));
  }

  function welcome() {
    return [h('h2', {}, t('welcome')), h('p', { class: 'lead' }, t('welcomeLead')),
      INFO.existing ? h('div', { class: 'box' }, t('existing', INFO.existingUrl)) : null,
      h('label', {}, t('checks')),
      h('div', { class: INFO.admin ? 'ok' : 'bad' }, (INFO.admin ? '✓ ' : '✗ ') + t(INFO.admin ? 'isAdmin' : 'notAdmin')),
      INFO.existing ? null : h('div', { class: INFO.serverOs ? 'ok' : 'bad' }, (INFO.serverOs ? '✓ ' : '✗ ') + t(INFO.serverOs ? 'isServer' : 'notServer')),   // SETUP-030
      INFO.existing ? null : h('div', { class: INFO.portFree ? 'ok' : '' }, (INFO.portFree ? '✓ ' : 'ℹ ') + t(INFO.portFree ? 'portOk' : 'portBusy')),
      // SETUP-C90 (owner: "the first step is to update with one click"): already installed — one button updates it
      errBox(), nav(INFO.admin && INFO.existing ? h('button', { class: 'primary', id: 'go', onclick: () => { step = 6; install(); } }, t('update'))
        : INFO.admin && INFO.serverOs ? h('button', { class: 'primary', onclick: next }, t('next')) : null)];
  }
  function company() {
    const logo = h('input', { type: 'file', accept: 'image/png,image/jpeg', id: 'logo', onchange: () => { const f = logo.files[0]; if (!f) return; const rd = new FileReader(); rd.onload = () => { A.logo = rd.result; render(); }; rd.readAsDataURL(f); } });
    return [h('h2', {}, t('company')), h('p', { class: 'lead' }, t('companyLead')),
      field('product', t('product'), { autofocus: true }, t('productHint')), field('slogan', t('slogan'), {}, t('sloganHint')), field('company', t('companyName')),
      h('div', { class: 'row2' }, h('div', {}, field('phone', t('phone'), { dir: 'ltr' })), h('div', {}, field('email', t('email'), { dir: 'ltr', type: 'email' }))),
      field('website', t('website'), { dir: 'ltr' }),
      h('div', { class: 'row2' }, h('div', {}, field('color', t('color'), { type: 'color' })), h('div', {}, field('accent', t('accent'), { type: 'color' }))),
      h('label', { for: 'logo' }, t('logo')), logo, A.logo ? h('img', { src: A.logo, alt: '', style: 'max-height:40px;margin-top:6px' }) : null,
      errBox(), nav(h('button', { class: 'primary', onclick: next }, t('next')))];
  }
  // SETUP-040: the drive with the most free space (not the Windows drive) is suggested; the person may pick another
  // drive or write any folder (owner, after the first real installation). More places are added later in the admin site.
  const join = (d) => d + (/[\\/]$/.test(d) ? '' : '\\') + 'OnlineBackup';
  function storage() {
    if (!A.dataRoot) A.dataRoot = INFO.suggestedDrive ? join(INFO.suggestedDrive) : '';
    const drives = INFO.drives || [];
    const input = h('input', { id: 'folder', class: 'ltr', value: A.dataRoot, oninput: () => { A.dataRoot = input.value; } });
    return [h('h2', {}, t('storage')), h('p', {}, t('storageLead')),
      drives.length ? h('div', { class: 'drives' }, drives.map((d) => h('label', { style: 'font-weight:normal;display:flex;gap:8px;align-items:center' },
        h('input', { type: 'radio', name: 'drv', style: 'width:auto', checked: A.dataRoot.toUpperCase().startsWith(d.name.toUpperCase()),
          onchange: () => { A.dataRoot = join(d.name); render(); } }),
        h('span', { class: 'ltr' }, d.name), ' — ', t('free', d.freeGB, d.totalGB), d.system ? ' (' + t('system') + ')' : '', d.name === INFO.suggestedDrive ? ' · ' + t('recommended') : ''))) : null,
      h('label', { for: 'folder' }, t('folder')), input, h('div', { class: 'hint' }, t('folderHint')),
      errBox(), nav(h('button', { class: 'primary', onclick: next }, t('next')))];
  }
  function address() {
    if (!INFO.publicIp && A.hostMode === 'ip') A.hostMode = 'domain';
    const radio = (mode, label) => h('label', { style: 'font-weight:normal;display:flex;gap:8px;align-items:center' },
      h('input', { type: 'radio', name: 'hm', style: 'width:auto', checked: A.hostMode === mode, onchange: () => { A.hostMode = mode; if (mode === 'domain' && A.hostName === INFO.publicIp) A.hostName = ''; render(); } }), label);
    const local = (INFO.localIps || [])[0] || '';
    return [h('h2', {}, t('address')), h('p', { class: 'lead' }, t('addressLead')),
      h('label', {}, t('hostChoice')),
      INFO.publicIp ? radio('ip', t('useIp', INFO.publicIp)) : h('div', { class: 'hint' }, t('noIp')),
      radio('domain', t('useDomain')),
      A.hostMode === 'domain' ? field('hostName', t('host'), { dir: 'ltr', placeholder: 'backup.mycompany.com' }) : null,
      field('port', t('port'), { type: 'number', min: 1024, max: 65535, dir: 'ltr', style: 'max-width:140px' }, t('portHint')),
      h('div', { class: 'box' }, h('b', {}, t('router')), h('div', { html: t('routerText', esc(A.port), esc(local)) })),
      errBox(), nav(h('button', { class: 'primary', onclick: next }, t('next')))];
  }
  function admin() {
    return [h('h2', {}, t('admin')), h('p', { class: 'lead' }, t('adminLead')),
      field('adminLogin', t('login'), { dir: 'ltr', autocomplete: 'username' }),
      h('div', { class: 'row2' }, h('div', {}, field('adminPassword', t('password'), { type: 'password', autocomplete: 'new-password' }, t('pwHint'))), h('div', {}, field('password2', t('password2'), { type: 'password', autocomplete: 'new-password' }))),
      field('alertEmail', t('alert'), { type: 'email', dir: 'ltr' }, t('alertHint')),
      errBox(), nav(h('button', { class: 'primary', onclick: next }, t('next')))];
  }
  function mail() {
    const sel = h('select', { id: 'mp', onchange: () => { A.mailProvider = sel.value; render(); } },
      ['none', 'gmail', 'm365', 'other'].map(v => h('option', { value: v, selected: A.mailProvider === v }, t({ none: 'mailNone', gmail: 'mailGmail', m365: 'mailM365', other: 'mailOther' }[v]))));
    const p = A.mailProvider;
    return [h('h2', {}, t('mail')), h('p', { class: 'lead' }, t('mailLead')), h('label', { for: 'mp' }, t('mail')), sel,
      p !== 'none' ? [field('smtpLogin', t('smtpLogin'), { type: 'email', dir: 'ltr' }), field('smtpPassword', t('smtpPassword'), { type: 'password', autocomplete: 'off' }, p === 'gmail' ? t('gmailHint') : p === 'm365' ? t('m365Hint') : null)] : null,
      p === 'other' ? h('div', { class: 'row2' }, h('div', {}, field('smtpHost', t('smtpHost'), { dir: 'ltr' })), h('div', {}, field('smtpPort', t('smtpPort'), { type: 'number', dir: 'ltr' }))) : null,
      errBox(), nav(h('button', { class: 'primary', onclick: next }, t('next')))];
  }
  let progress = null;
  function summary() {
    if (progress) return progressView();
    const row = (k, v) => h('tr', {}, h('td', { style: 'padding-block:4px;padding-inline:0 12px;color:var(--muted)' }, t(k)), h('td', { class: 'ltr', style: 'padding:4px 0' }, v));
    const host = A.hostMode === 'ip' ? (INFO.publicIp || A.hostName) : A.hostName;
    return [h('h2', {}, t('summary')), h('p', { class: 'lead' }, t('summaryLead')),
      INFO.existing ? h('div', { class: 'box' }, t('existing', INFO.existingUrl)) :
        [h('table', {}, row('sProduct', A.product + ' — ' + A.company), row('sStorage', /^[A-Za-z]:[\\/]?$/.test(A.dataRoot || '') ? join(A.dataRoot) : (A.dataRoot || '')), row('sAddress', 'https://' + host + ':' + A.port),
          row('sAdmin', A.adminLogin), row('sMail', A.mailProvider === 'none' ? t('none') : A.smtpLogin)),
        h('div', { class: 'box' }, h('b', {}, t('willDo')), h('ol', { style: 'margin:6px 0 0' }, tl('willList').map(x => h('li', {}, x.replace('{0}', A.port)))), h('div', { class: 'hint' }, t('noIis')))],
      errBox(), nav(h('button', { class: 'primary', id: 'go', onclick: install }, t(INFO.existing ? 'update' : 'install')))];
  }
  async function install() {
    const r = await call('install', A);
    if (r.errors && r.errors.length) { errors = r.errors; render(); return; }
    progress = { lines: [], done: false }; render();
    while (!progress.done) { await new Promise(res => setTimeout(res, 700)); progress = await call('progress'); render(); }
  }
  function progressView() {
    const lines = progress.lines || [];
    if (!progress.done) return [h('h2', {}, t(INFO.existing ? 'updating' : 'installing')), h('div', { class: 'log', 'aria-live': 'polite' }, lines.map((l, i) => h('div', { class: i === lines.length - 1 ? 'cur' : '' }, msg(l))))];
    if (progress.error) { const e = progress.error; progress = null; errors = [e]; return [h('h2', { class: 'bad' }, t('failed')), h('p', {}, msg(e)), h('p', { class: 'hint' }, t('failedHint')), nav(h('button', { class: 'primary', onclick: () => render() }, t('back')))]; }
    const r = progress.result;
    const local = (INFO.localIps || [])[0] || '';
    return [h('h2', { class: 'ok' }, t(r.update ? 'updatedTitle' : 'doneTitle')), h('div', { class: 'log' }, lines.map(l => h('div', {}, msg(l)))),
      h('div', { class: 'box' }, h('b', {}, t('nextSteps')), h('ol', { style: 'margin:6px 0 0' }, h('li', {}, h('a', { href: r.adminUrl, target: '_blank', rel: 'noopener', class: 'ltr' }, r.adminUrl), ' — ', t('step1')), h('li', {}, t('step2')),
        r.update ? null : h('li', { html: esc(t('step3', A.port)) + '<br>' + t('routerText', esc(A.port), esc(local)) })), h('div', { class: 'hint' }, t('certWarn'))),
      r.pin ? h('div', { class: 'hint' }, t('pinLabel') + ': ', h('code', {}, r.pin)) : null,
      h('div', { class: 'nav' }, h('button', { onclick: () => window.print() }, t('print')), h('span', { class: 'space' }),
        h('button', { class: 'primary', onclick: () => closeSetup('https://localhost:' + (A.port || 8443) + '/admin') }, t('finish')))];
  }

  const sel = document.getElementById('lang');
  I18N.load((() => { try { return localStorage.getItem('obLang') || 'en'; } catch (e) { return 'en'; } })()).then(() => {
    for (const [k, v] of Object.entries(I18N.names)) sel.append(h('option', { value: k, selected: k === I18N.lang }, v));
    sel.onchange = async () => { try { localStorage.setItem('obLang', sel.value); } catch (e) { } await I18N.load(sel.value); if (INFO) render(); };
    return call('info');
  }).then(async (i) => {
    INFO = i; if (i.publicIp) A.hostName = i.publicIp;
    // PORTAL-020: the package from the partner portal brings the company's product — the wizard is filled in
    if (i.preset) {
      for (const k of ['product', 'slogan', 'company', 'phone', 'email', 'website', 'color', 'accent', 'logo', 'language']) if (i.preset[k]) A[k] = i.preset[k];
      let chosen = null; try { chosen = localStorage.getItem('obLang'); } catch (e) { }
      if (!chosen && A.language && A.language !== I18N.lang) { await I18N.load(A.language); sel.value = I18N.lang; }
    }
    render();
  }).catch((e) => { console.error(e); document.getElementById('card').textContent = 'Error'; });
})();
