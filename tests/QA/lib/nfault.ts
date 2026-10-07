// Agent N (failure matrix) helpers — additive: world.ts and fault.ts are not changed.
// - Line: the fault-injecting proxy (runner/nproxy.mjs) with its own log as the evidence of each fault
// - the agent as an asynchronous process with its output captured (so the test can act while it runs)
// - set settings changed as the technician changes them in the admin site (pre-commands, temporary folder)
// - the oracle helpers: the server's runs of a set, the points, files under a folder
import { spawn, spawnSync, ChildProcess, execSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { World, Agent, CUSTOMER_PASSWORD, goldenDataset, Manifest } from './world';
import { AGENT_DLL, smallDisk } from './fault';

export class Line {
  url = ''; ctlUrl = ''; log: string; private p?: ChildProcess;
  constructor(private world: World, name = 'line') { this.log = path.join(world.dir, name + '-proxy.log'); }
  async start() {
    const port = new URL(this.world.url).port;
    this.p = spawn(process.execPath, [path.join(__dirname, '../runner/nproxy.mjs'), port, this.log], { stdio: ['ignore', 'pipe', 'inherit'] });
    const m = await new Promise<RegExpExecArray>((r) => this.p!.stdout!.on('data', (d) => { const x = /listening (\d+) control (\d+)/.exec(String(d)); if (x) r(x); }));
    this.url = 'http://localhost:' + m[1]; this.ctlUrl = 'http://127.0.0.1:' + m[2];
    return this;
  }
  async ctl(cmd: string) { const r = await fetch(this.ctlUrl + cmd, { method: 'POST' }); return await r.json(); }
  async stats(): Promise<any> { const r = await fetch(this.ctlUrl + '/stats'); return await r.json(); }
  events(ev?: string): any[] { if (!fs.existsSync(this.log)) return []; return fs.readFileSync(this.log, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)).filter((e) => !ev || e.ev === ev); }
  stop() { this.p?.kill('SIGKILL'); }
}

export type Ran = { code: number, out: string, ms: number };
/** The agent command line as an asynchronous process with its output captured; `wrap` goes in front. */
export function agentAsync(ag: Agent, args: string[], opts: { wrap?: string[], env?: NodeJS.ProcessEnv } = {}) {
  const wrap = opts.wrap || [];
  const cmd = [...wrap, 'dotnet', AGENT_DLL, args[0], '--home', ag.home, ...args.slice(1)];
  const t0 = Date.now(); const chunks: string[] = [];
  const p = spawn(cmd[0], cmd.slice(1), { env: { ...process.env, ...ag.world.env, ...(opts.env || {}) } });
  p.stdout!.on('data', (d) => chunks.push(String(d))); p.stderr!.on('data', (d) => chunks.push(String(d)));
  const done = new Promise<Ran>((r) => p.on('exit', (c, sig) => {
    const out = chunks.join(''); fs.appendFileSync(ag.log, '$ ' + [...wrap, 'agent', ...args].join(' ') + ' (async)\n' + out + '\n');
    r({ code: c ?? (sig ? -2 : -1), out, ms: Date.now() - t0 });
  }));
  return { p, done };
}
export const backupAsync = (ag: Agent, setId: string, opts: { wrap?: string[], env?: NodeJS.ProcessEnv } = {}) => agentAsync(ag, ['backup', '--set', setId], opts);
export const restoreAsync = (ag: Agent, setId: string, target: string, more: string[] = [], opts: { wrap?: string[], env?: NodeJS.ProcessEnv } = {}) =>
  agentAsync(ag, ['restore', '--set', setId, '--password', CUSTOMER_PASSWORD, '--target', target, ...more], opts);

/** The result word of a backup run's output (BS_STOP_…). */
export function result(out: string) { const m = /^(BS_STOP_[A-Z_]+) /m.exec(out); return m ? m[1] : '(none)'; }

/** A set setting changed as the technician changes it in the admin site (the set's XML, like World.setBandwidth). */
export async function editSet(world: World, login: string, setId: string, change: (xml: string) => string) {
  const got = await world.adminApi('GET', 'users/' + login + '/sets/' + setId);
  const xml = /<f n="set">([\s\S]*?)<\/f>/.exec(got.text)![1].replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
  const next = change(xml);
  if (next === xml) throw new Error('the set XML did not change:\n' + xml.slice(0, 1500));
  const r = await world.adminApi('POST', 'users/' + login + '/sets/' + setId, { set: next });
  if (r.status !== 200) throw new Error('set update failed: ' + r.text);
  return next;
}
export const xmlAttr = (s: string) => s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** The backup runs the server recorded for one set (newest first). */
export async function setRuns(world: World, login: string, setId: string) { return (await world.runs(login)).filter((r) => r.set === setId && r.kind === 'Backup'); }

/** The restore points the server lists for a set. */
export function points(ag: Agent, setId: string) { return ag.cli(['points', '--set', setId]).out.trim().split('\n').map((l) => l.trim()).filter((l) => /^\d+/.test(l)); }

/** The golden dataset without its 20 MB file (~3.4 MB): for the slow-line scenarios. */
export function smallGolden(root: string, seed = 1): Manifest {
  const m = goldenDataset(root, seed); fs.rmSync(path.join(root, 'Binary/large.bin')); m.delete('Binary/large.bin'); return m;
}

export function sh(cmd: string) { try { return execSync(cmd, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(); } catch (e: any) { return 'ERR ' + (e.status ?? '') + ' ' + String(e.stderr || e.message).trim(); } }
export function df(dir: string) { return sh('df -B1 --output=size,used,avail,pcent,target "' + dir + '" | tail -1'); }
export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));
/** Waits until fn() is true (polling), or the time is up; returns whether it happened. */
export async function until(fn: () => boolean | Promise<boolean>, ms: number, every = 250) { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await sleep(every); } return false; }

/** Mounts a fresh tmpfs of `mb` MB at dir (false when not allowed; root or passwordless sudo — see smallDisk). */
export function tmpfs(dir: string, mb: number) { return smallDisk(dir, mb); }
/** Fills the file system of dir to the last byte with a filler file; returns its path. */
export function fill(dir: string, name = 'filler.bin') {
  const f = path.join(dir, name); spawnSync('sh', ['-c', 'dd if=/dev/zero of="' + f + '" bs=1M 2>/dev/null; dd if=/dev/zero of="' + f + '" bs=4k oflag=append conv=notrunc 2>/dev/null; true']); return f;
}
/** The root user's DAC override dropped (bounding set): permission bits then apply to this process as to any user. */
// root reads anything: its DAC override is dropped so the permission bits apply to it. Not root (the CI runner): the bits
// apply already, and setpriv cannot change the bounding set there ("Operation not permitted" — run 12) — nothing to drop.
export const NO_DAC: string[] = process.getuid && process.getuid() === 0 ? ['setpriv', '--bounding-set=-dac_override,-dac_read_search', '--'] : [];

/** Evidence kept after the run (the world folder is removed when a test passes): tests/QA/night/n/evidence/<name>.json */
export function keep(name: string, obj: unknown) {
  const d = path.join(__dirname, '../night/n/evidence'); fs.mkdirSync(d, { recursive: true });
  fs.writeFileSync(path.join(d, name + '.json'), JSON.stringify(obj, null, 1));
}
