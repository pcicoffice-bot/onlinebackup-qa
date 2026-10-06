// Fault-injection helpers for the failure-recovery scenarios (additive: world.ts is not changed).
// - the agent as its own process with a wrapper in front (a moved clock through libfaketime, a private mount namespace)
// - a small disk (tmpfs) for the computer's side
// - "no half file left as if whole": every file present under a folder must be byte-identical to the original
import { spawn, spawnSync, execSync, ChildProcess } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as crypto from 'crypto';
import { Agent, Manifest, CUSTOMER_PASSWORD } from './world';

export const AGENT_DLL = path.join(process.env.QA_PRODUCT ? path.resolve(process.env.QA_PRODUCT) : path.resolve(__dirname, '../../..'), 'src/Agent/bin/Debug/net8.0/OnlineBackup.Agent.dll');

/** The agent command line as its own process; `wrap` goes in front (e.g. ['faketime', '-f', '+3h']). */
export function agentProcess(ag: Agent, args: string[], wrap: string[] = []): ChildProcess {
  const out = fs.openSync(ag.log, 'a');
  fs.appendFileSync(ag.log, '$ ' + [...wrap, 'agent', ...args].join(' ') + ' (process)\n');
  const cmd = [...wrap, 'dotnet', AGENT_DLL, args[0], '--home', ag.home, ...args.slice(1)];
  // Q16 (night): a wrapper such as faketime forks the agent and does not pass a signal on — killing the wrapper left the
  // agent running (F13 left three agent services per run, hours later; its 2nd and 3rd phase ran beside the 1st).
  // The process gets its own group, and kill() signals the whole group.
  const p = spawn(cmd[0], cmd.slice(1), { env: { ...process.env, ...ag.world.env }, stdio: ['ignore', out, out], detached: true });
  const own = p.kill.bind(p);
  p.kill = (sig?: NodeJS.Signals | number) => { try { process.kill(-p.pid!, sig ?? 'SIGTERM'); return true; } catch { return own(sig); } };
  return p;
}

/** The agent command line, waited for; `wrap` goes in front. */
export function agentRun(ag: Agent, args: string[], wrap: string[] = []) {
  const cmd = [...wrap, 'dotnet', AGENT_DLL, args[0], '--home', ag.home, ...args.slice(1)];
  const r = spawnSync(cmd[0], cmd.slice(1), { encoding: 'utf8', env: { ...process.env, ...ag.world.env }, timeout: 15 * 60 * 1000 });
  const out = (r.stdout || '') + (r.stderr || '');
  fs.appendFileSync(ag.log, '$ ' + [...wrap, 'agent', ...args].join(' ') + '\n' + out + '\n');
  return { code: r.status ?? -1, out };
}

/** A restore as its own process (to be interrupted by a fault). */
export function restoreProcess(ag: Agent, setId: string, target: string, more: string[] = []) {
  const chunks: string[] = [];
  const p = spawn('dotnet', [AGENT_DLL, 'restore', '--home', ag.home, '--set', setId, '--password', CUSTOMER_PASSWORD, '--target', target, ...more], { env: { ...process.env, ...ag.world.env } });
  p.stdout!.on('data', (d) => chunks.push(String(d))); p.stderr!.on('data', (d) => chunks.push(String(d)));
  const done = new Promise<{ code: number, out: string }>((r) => p.on('exit', (c) => { const out = chunks.join(''); fs.appendFileSync(ag.log, '$ agent restore (process)\n' + out + '\n'); r({ code: c ?? -1, out }); }));
  return { p, done };
}

/** A small disk mounted at `dir` (needs root / CAP_SYS_ADMIN); false when this machine does not allow it. */
export function smallDisk(dir: string, mb: number): boolean {
  fs.mkdirSync(dir, { recursive: true });
  try { execSync('mount -t tmpfs -o size=' + mb + 'm tmpfs "' + dir + '"', { stdio: 'pipe' }); return true; } catch { return false; }
}
export function unmount(dir: string) { try { execSync('umount -l "' + dir + '"', { stdio: 'pipe' }); } catch { } }

export function hasProgram(name: string) { return spawnSync('sh', ['-c', 'command -v ' + name]).status === 0; }

/** Files of `rel` names under `root` that exist but are NOT byte-identical to the original (a half file passing as whole). */
export function notWhole(expected: Manifest, actual: Manifest): string[] {
  const bad: string[] = [];
  for (const [k, v] of actual) { const e = expected.get(k); if (e && e.sha256 !== v.sha256) bad.push(k + ' (' + v.size + ' of ' + e.size + ' bytes)'); }
  return bad;
}

/** Leftover temporary names of a restore in the customer's folder. */
// the restore's own temporary names: "<name>.<8 hex>.ob-restoring" (native, bug 78), the stage folder ".ob-restoring-<id>"
// (restic, bug 48) and the older "<name>.restoring" — since bug 78 the old pattern alone saw none of them (night QA)
export const TEMP_NAME = /\.ob-restoring$|(^|[\/\\])\.ob-restoring-|\.restoring$|\.part$|\.tmp$|\.obj$/;
export function leftovers(m: Manifest) { return [...m.keys()].filter((k) => TEMP_NAME.test(k)); }

/** Large files of random content (so each must really travel): makes a restore last long enough to be interrupted. */
export function bigFiles(root: string, count: number, mb: number) {
  for (let i = 0; i < count; i++) {
    const f = path.join(root, 'Big', 'big-' + i + '.bin'); fs.mkdirSync(path.dirname(f), { recursive: true });
    const fd = fs.openSync(f, 'w');
    for (let j = 0; j < mb; j++) fs.writeSync(fd, crypto.randomBytes(1024 * 1024));
    fs.closeSync(fd);
  }
}

/** The restore runs the server recorded for a customer (kind "Restore"), newest first (as world.runs() gives them). */
export function restoreRuns(runs: Record<string, string>[]) { return runs.filter((r) => r.kind === 'Restore'); }

/** How many files are under a folder now (names only, safe while a restore renames files). */
export function fileCount(dir: string) {
  try { return fs.readdirSync(dir, { recursive: true, withFileTypes: true }).filter((e) => e.isFile()).length; } catch { return 0; }
}
