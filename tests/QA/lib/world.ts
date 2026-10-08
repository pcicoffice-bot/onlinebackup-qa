// The QA "world": the real product, started and used from outside.
// - the real server program (the same build that is packaged), on a fresh data folder, with a throw-away licence
// - the real agent program (the same one the Windows service runs), as a separate process that can be killed
// - a golden dataset with a SHA-256 manifest, compared after every restore (the oracle — never the product's own word)
// Nothing here reads or changes the product's internal state, except where a precondition needs a fixture that a
// person would also create by hand (an administrator's authenticator secret, a licence): those are marked FIXTURE.
import { spawn, spawnSync, ChildProcess } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import * as crypto from 'crypto';
import * as net from 'net';

export const REPO = path.resolve(__dirname, '../../..');
/** QA_PRODUCT: test another build of the product (e.g. the previous version, to prove a regression test fails there). */
const PRODUCT = process.env.QA_PRODUCT ? path.resolve(process.env.QA_PRODUCT) : REPO;
export const ADMIN = { login: 'admin', password: 'Admin-Pass-123', totp: 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP' };
export const CUSTOMER_PASSWORD = 'Customer-Pass-1';
const SERVER_DLL = path.join(PRODUCT, 'src/Server/bin/Debug/net8.0/OnlineBackup.Server.dll');
const AGENT_DLL = path.join(PRODUCT, 'src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll');

/** Builds the server and the agent once per QA run (the same sources that are packaged). */
export function build() {
  for (const p of ['src/Server', 'src/Agent']) {
    const r = spawnSync('dotnet', ['build', p, '-nologo', '-v', 'q'], { cwd: PRODUCT, encoding: 'utf8' });
    if (r.status !== 0) throw new Error('build of ' + p + ' failed:\n' + r.stdout + r.stderr);
  }
}

export function totp(secret: string, at = Date.now()) {
  const A = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
  for (const ch of secret.replace(/=+$/, '')) bits += A.indexOf(ch).toString(2).padStart(5, '0');
  const key = Buffer.from(bits.match(/.{8}/g)!.map((b) => parseInt(b, 2)));
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(Math.floor(at / 30000)));
  const hm = crypto.createHmac('sha1', key).update(msg).digest(); const o = hm[19] & 15;
  return String(((hm.readUInt32BE(o) & 0x7fffffff) % 1000000)).padStart(6, '0');
}

/** H-02: the server takes each authenticator code once (as a person's next sign-in takes the next code from the phone).
 *  The code of the next unused 30-second step on this server (the next step is accepted for clock drift); when both
 *  are used, waits for a new step. */
const usedStep = new Map<string, number>();
export async function freshTotp(secret: string, server: string) {
  for (;;) {
    const now = Math.floor(Date.now() / 30000), last = usedStep.get(server + '|' + secret) ?? -1;
    const step = Math.max(now, last + 1);
    if (step <= now + 1) { usedStep.set(server + '|' + secret, step); return totp(secret, step * 30000); }
    await new Promise((r) => setTimeout(r, 1000));
  }
}

function freePort(): Promise<number> {
  return new Promise((res) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => res(p)); }); });
}

function waitFor(file: string, text: string, ms = 60000) {
  const until = Date.now() + ms;
  return new Promise<void>((res, rej) => {
    const t = setInterval(() => {
      if (fs.existsSync(file) && fs.readFileSync(file, 'utf8').includes(text)) { clearInterval(t); res(); }
      else if (Date.now() > until) { clearInterval(t); rej(new Error('timeout waiting for "' + text + '" in ' + file + ':\n' + (fs.existsSync(file) ? fs.readFileSync(file, 'utf8').slice(-2000) : '(no file)'))); }
    }, 200);
  });
}

function run(dll: string, args: string[], opts: { env?: NodeJS.ProcessEnv, allowFail?: boolean } = {}) {
  const r = spawnSync('dotnet', [dll, ...args], { encoding: 'utf8', env: { ...process.env, ...(opts.env || {}) }, timeout: 15 * 60 * 1000 });
  const out = (r.stdout || '') + (r.stderr || '');
  if (r.status !== 0 && !opts.allowFail) throw new Error('dotnet ' + path.basename(dll) + ' ' + args.join(' ') + ' → exit ' + r.status + '\n' + out);
  return { code: r.status ?? -1, out };
}

export class World {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), 'obqa-'));
  /** Where the customers' backups are kept (a test can put a small disk there). */
  usersDir = path.join(this.dir, 'users');
  sys = path.join(this.dir, 'sys');
  url = '';
  server?: ChildProcess;
  serverLog = path.join(this.dir, 'server.log');
  env: NodeJS.ProcessEnv = {};
  agents: Agent[] = [];

  /** Fresh server: install (init), an administrator with two-step, a licence, started. */
  async start() {
    run(SERVER_DLL, ['init', '--system-home', this.sys, '--admin', ADMIN.login, '--password', ADMIN.password, '--host', 'localhost', '--user-home', this.usersDir + '|UNLIMITED|100']);
    // FIXTURE: the administrator's authenticator secret, as if the first sign-in had set it up on a phone
    const sx = path.join(this.sys, 'conf', 'system.xml');
    fs.writeFileSync(sx, fs.readFileSync(sx, 'utf8').replace(/(<ADMIN [^>]*?)TOTP_SECRET="[^"]*"/, '$1TOTP_SECRET="' + ADMIN.totp + '"'));
    // FIXTURE: a licence with every module, signed by a throw-away key of this run (as the vendor would issue one)
    const key = path.join(this.dir, 'lic.key');
    const pub = run(SERVER_DLL, ['license-keygen', '--out', key]).out.trim().split('\n').pop()!.trim();
    this.env = { OB_LICENSE_PUBKEY: pub };
    const sid = run(SERVER_DLL, ['server-id', '--system-home', this.sys]).out.trim().split('\n').pop()!.trim();
    const lic = run(SERVER_DLL, ['license-issue', '--key', key, '--server-id', sid, '--company', 'QA IT', '--users', '100', '--storage-gb', '1000', '--days', '30'], { env: this.env }).out.trim().split('\n').pop()!.trim();
    const x = fs.readFileSync(sx, 'utf8');
    if (!/<LICENSE KEY="" \/>/.test(x)) throw new Error('system.xml has no empty LICENSE element');
    fs.writeFileSync(sx, x.replace('<LICENSE KEY="" />', '<LICENSE KEY="' + lic + '" />'));
    await this.startServer();
    return this;
  }

  async startServer() {
    if (!this.url) this.url = 'http://localhost:' + (await freePort());
    fs.appendFileSync(this.serverLog, '\n==== server start ' + new Date().toISOString() + '\n');
    const out = fs.openSync(this.serverLog, 'a');
    const mark = fs.statSync(this.serverLog).size;
    this.server = spawn('dotnet', [SERVER_DLL, 'run', '--system-home', this.sys, '--prefix', this.url + '/'], { env: { ...process.env, ...this.env }, stdio: ['ignore', out, out] });
    const until = Date.now() + 60000;
    while (Date.now() < until) { if (fs.readFileSync(this.serverLog, 'utf8').slice(mark).includes('listening')) return; await new Promise((r) => setTimeout(r, 200)); }
    throw new Error('the server did not start:\n' + fs.readFileSync(this.serverLog, 'utf8').slice(-3000));
  }

  /** Kill -9 of the server process (a crash or power cut of the server). */
  killServer() { this.server?.kill('SIGKILL'); this.server = undefined; }

  /** A customer company (what the client program's sign-up does; the CLI is the same server code). */
  addCustomer(login: string, quotaGb = 5) {
    run(SERVER_DLL, ['adduser', '--system-home', this.sys, '--login', login, '--password', CUSTOMER_PASSWORD, '--quota-gb', String(quotaGb), '--email', 'it@example.invalid'], { env: this.env });
  }

  agent(login: string, computer = 'QA-PC') { const a = new Agent(this, login, computer); this.agents.push(a); return a; }

  /** The administrator's API (for preconditions and the oracle's second look; journeys use the web pages). */
  async adminApi(method: string, p: string, body?: Record<string, string | number>) {
    if (!this._ses) {
      const r = await fetch(this.url + '/api/admin/login', { method: 'POST', headers: { 'Content-Type': 'application/xml' }, body: xmlMsg({ login: ADMIN.login, password: ADMIN.password, otp: await freshTotp(ADMIN.totp, this.url) }) });
      const t = await r.text(); const m = /<f n="session">([^<]+)/.exec(t); if (!m) throw new Error('admin login failed: ' + r.status + ' ' + t);
      this._ses = m[1];
    }
    const r = await fetch(this.url + '/api/admin/' + p, { method, headers: { 'Content-Type': 'application/xml', 'X-Session': this._ses }, body: method === 'GET' ? undefined : xmlMsg(body || {}) });
    return { status: r.status, text: await r.text() };
  }
  private _ses = '';

  /** Precondition: an upload limit on a set, as the technician sets it in the set's Resources tab (J2 proves that tab). */
  async setBandwidth(login: string, setId: string, kbps: number) {
    const got = await this.adminApi('GET', 'users/' + login + '/sets/' + setId);
    const xml = /<f n="set">([\s\S]*?)<\/f>/.exec(got.text)![1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
    const r = await this.adminApi('POST', 'users/' + login + '/sets/' + setId, { set: xml.replace(/BANDWIDTH_KBPS="\d+"/, 'BANDWIDTH_KBPS="' + kbps + '"') });
    if (r.status !== 200) throw new Error('set update failed: ' + r.text);
  }

  /** The runs the server keeps (the tasks page reads the same list) — for waiting, never as the only proof. */
  async runs(login: string) {
    const r = await this.adminApi('GET', 'tasks?hours=24');
    return [...r.text.matchAll(/<i>([\s\S]*?)<\/i>/g)].map((m) => Object.fromEntries([...m[1].matchAll(/<f n="([^"]+)">([^<]*)<\/f>/g)].map((x) => [x[1], x[2]]))).filter((x) => x.login === login);
  }
  async live() { const r = await this.adminApi('GET', 'live'); return [...r.text.matchAll(/<f n="set">([^<]*)/g)].map((m) => m[1]); }

  async stop() {
    for (const a of this.agents) a.stopService();
    this.killServer();
  }
}

export function xmlMsg(d: Record<string, string | number>) {
  const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  return '<m>' + Object.entries(d).map(([k, v]) => '<f n="' + esc(k) + '">' + esc(String(v)) + '</f>').join('') + '</m>';
}

export class Agent {
  home: string; log: string; service?: ChildProcess;
  constructor(public world: World, public login: string, public computer: string) {
    this.home = path.join(world.dir, 'agent-' + login + '-' + computer);
    this.log = path.join(world.dir, 'agent-' + login + '-' + computer + '.log');
  }
  cli(args: string[], allowFail = false) {
    const r = run(AGENT_DLL, [...args.slice(0, 1), '--home', this.home, ...args.slice(1)], { allowFail, env: this.world.env });
    fs.appendFileSync(this.log, '$ agent ' + args.join(' ') + '\n' + r.out + '\n');
    return r;
  }
  register(via?: string) { return this.cli(['register', '--server', (via || this.world.url) + '/', '--login', this.login, '--password', CUSTOMER_PASSWORD, '--computer', this.computer]); }
  /** A backup set, as the customer creates it in the client program. Returns its id. */
  addSet(name: string, sources: string[], extra: string[] = []) {
    const r = this.cli(['addset', '--password', CUSTOMER_PASSWORD, '--name', name, ...sources.flatMap((s) => ['--source', s]), '--keytype', 'PASSWORD', ...extra]);
    return r.out.trim().split('\n').pop()!.trim();
  }
  sets() { return this.cli(['sets']).out.trim().split('\n').filter((l) => /^\d+\t/.test(l)).map((l) => { const [id, name, src] = l.split('\t'); return { id, name, sources: (src || '').split(';') }; }); }
  backup(setId: string) { return this.cli(['backup', '--set', setId], true); }
  /** A backup as its own process (to be killed). */
  backupProcess(setId: string) {
    const out = fs.openSync(this.log, 'a');
    return spawn('dotnet', [AGENT_DLL, 'backup', '--home', this.home, '--set', setId], { env: { ...process.env, ...this.world.env }, stdio: ['ignore', out, out] });
  }
  restore(setId: string, target: string, more: string[] = []) { return this.cli(['restore', '--set', setId, '--password', CUSTOMER_PASSWORD, '--target', target, ...more], true); }
  /** The agent as the Windows service runs it: a loop that runs scheduled and requested backups. */
  startService() {
    const out = fs.openSync(this.log, 'a');
    this.service = spawn('dotnet', [AGENT_DLL, 'service', '--home', this.home], { env: { ...process.env, ...this.world.env }, stdio: ['ignore', out, out] });
    return this.service;
  }
  killService() { this.service?.kill('SIGKILL'); this.service = undefined; }
  stopService() { this.service?.kill('SIGTERM'); this.service = undefined; }
}

// ------------------------------------------------------------------ golden dataset + oracle

export type Manifest = Map<string, { size: number, sha256: string }>;

/** The golden dataset: small, large, empty, binary, text, Hebrew and Unicode names, spaces, deep and empty folders, duplicates. */
export function goldenDataset(root: string, seed = 1) {
  const rnd = crypto.createHash('sha256').update('seed' + seed).digest();
  const bytes = (n: number, s: number) => { const b = Buffer.alloc(n); let h = crypto.createHash('sha256').update(rnd).update(String(s)).digest(); for (let i = 0; i < n; i += 32) { h.copy(b, i); h = crypto.createHash('sha256').update(h).digest(); } return b; };
  const w = (rel: string, data: Buffer | string) => { const f = path.join(root, rel); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, data); };
  w('Documents/letter.txt', 'Dear customer,\nthis is a test letter.\n');
  w('מסמכים/מכתב ללקוח.txt', 'שלום עולם — מכתב בעברית\n');
  w('מסמכים/תיקייה עם רווחים/דוח שנתי 2026.csv', 'שנה,סכום\n2026,100\n');
  w('Unicode/日本語 ファイル.txt', 'こんにちは');
  w('Unicode/émoji 🎉 file.txt', 'party');
  w('Folder with spaces/file with spaces.txt', 'a b c');
  w('Empty/zero.bin', Buffer.alloc(0));
  w('Binary/random.bin', bytes(3 * 1024 * 1024 + 17, 1));
  w('Binary/large.bin', bytes(20 * 1024 * 1024, 2));
  w('Duplicates/a.txt', 'same content'); w('Duplicates/b.txt', 'same content');
  w('Deep/' + 'level/'.repeat(12) + 'deep.txt', 'deep');
  w('Long/' + 'a-very-long-file-name-'.repeat(8) + '.txt', 'long name');
  for (let i = 0; i < 150; i++) w('Many/file-' + String(i).padStart(3, '0') + '.csv', 'row,' + i + '\n');
  fs.mkdirSync(path.join(root, 'Empty folder'), { recursive: true });
  return manifest(root);
}

export function manifest(root: string): Manifest {
  const m: Manifest = new Map();
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) walk(f);
      else if (e.isFile()) { const b = fs.readFileSync(f); m.set(path.relative(root, f).split(path.sep).join('/'), { size: b.length, sha256: crypto.createHash('sha256').update(b).digest('hex') }); }
    }
  };
  if (fs.existsSync(root)) walk(root);
  return m;
}

/** How many files are under root, while something may still be writing there (a restore in progress): an entry that is
 *  renamed or removed between listing and looking is skipped, not an error. Only for waiting; the checks use manifest(). */
export function fileCount(root: string): number {
  let n = 0;
  const gone = (e: unknown) => (e as NodeJS.ErrnoException).code === 'ENOENT';
  const walk = (d: string) => {
    let es: fs.Dirent[];
    try { es = fs.readdirSync(d, { withFileTypes: true }); } catch (e) { if (gone(e)) return; throw e; }
    for (const e of es) { if (e.isDirectory()) walk(path.join(d, e.name)); else if (e.isFile()) n++; }
  };
  walk(root);
  return n;
}

/** Every difference between what was expected and what is there (empty = identical). */
export function compare(expected: Manifest, actual: Manifest): string[] {
  const d: string[] = [];
  for (const [k, v] of expected) { const a = actual.get(k); if (!a) d.push('MISSING ' + k); else if (a.sha256 !== v.sha256) d.push('DIFFERENT ' + k + ' (' + v.size + ' → ' + a.size + ' bytes)'); }
  for (const k of actual.keys()) if (!expected.has(k)) d.push('UNEXPECTED ' + k);
  return d;
}

/** Where a restore puts a source folder under a target (C:\x → target\C\x; /x → target/x), as the agent does. */
export function restoredPath(target: string, source: string) { return path.join(target, source.replace(':', '').replace(/^[\\/]+/, '')); }

// ------------------------------------------------------------------ the network between the computer and the server

/** A TCP proxy in front of the server, in its own process: the computer talks to it; cut() drops every connection. */
export class Net {
  url = ''; private p?: ChildProcess;
  constructor(private world: World) {}
  async start() {
    const port = new URL(this.world.url).port;
    this.p = spawn(process.execPath, [path.join(__dirname, '../runner/netproxy.mjs'), port], { stdio: ['ignore', 'pipe', 'inherit'] });
    const listening = await new Promise<string>((r) => this.p!.stdout!.on('data', (d) => { const m = /listening (\d+)/.exec(String(d)); if (m) r(m[1]); }));
    this.url = 'http://localhost:' + listening;   // the test server answers the host name localhost
    return this;
  }
  cut() { this.p?.kill('SIGUSR1'); }
  restore() { this.p?.kill('SIGUSR2'); }
  stop() { this.p?.kill('SIGKILL'); }
}
