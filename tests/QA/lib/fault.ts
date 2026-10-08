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

/** Q-PW5: a moved computer clock does not move the files' times. libfaketime also shifts the times that stat() returns —
 * the CI's .NET build read every file of P01 31 days newer and sent them all again (the coreutils and Python stat, and the
 * local .NET build, are not shifted, so it passed locally). With faketime in front, the files keep their real times. */
export function wrapEnv(wrap: string[]): NodeJS.ProcessEnv {
  return wrap[0] === 'faketime' ? { NO_FAKE_STAT: '1' } : {};
}

/** The agent command line as its own process; `wrap` goes in front (e.g. ['faketime', '-f', '+3h']). */
export function agentProcess(ag: Agent, args: string[], wrap: string[] = []): ChildProcess {
  const out = fs.openSync(ag.log, 'a');
  fs.appendFileSync(ag.log, '$ ' + [...wrap, 'agent', ...args].join(' ') + ' (process)\n');
  const cmd = [...wrap, 'dotnet', AGENT_DLL, args[0], '--home', ag.home, ...args.slice(1)];
  // Q16 (night): a wrapper such as faketime forks the agent and does not pass a signal on — killing the wrapper left the
  // agent running (F13 left three agent services per run, hours later; its 2nd and 3rd phase ran beside the 1st).
  // The process gets its own group, and kill() signals the whole group.
  const p = spawn(cmd[0], cmd.slice(1), { env: { ...process.env, ...ag.world.env, ...wrapEnv(wrap) }, stdio: ['ignore', out, out], detached: true });
  const own = p.kill.bind(p);
  p.kill = (sig?: NodeJS.Signals | number) => { try { process.kill(-p.pid!, sig ?? 'SIGTERM'); return true; } catch { return own(sig); } };
  return p;
}

/** The agent command line, waited for; `wrap` goes in front. */
export function agentRun(ag: Agent, args: string[], wrap: string[] = []) {
  const cmd = [...wrap, 'dotnet', AGENT_DLL, args[0], '--home', ag.home, ...args.slice(1)];
  const r = spawnSync(cmd[0], cmd.slice(1), { encoding: 'utf8', env: { ...process.env, ...ag.world.env, ...wrapEnv(wrap) }, timeout: 15 * 60 * 1000 });
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

// Mounting needs root (CAP_SYS_ADMIN). The CI runner is the non-root user "runner" with passwordless sudo: there only the
// mount / umount commands go through `sudo -n`, and the mount is given to the current user (uid, gid, the mode the
// folder had), so the product — still running as that normal user — writes there exactly as it would on a real disk.
// Neither root nor passwordless sudo: null, and the caller skips with its NOT TESTED reason.
const IS_ROOT = !!process.getuid && process.getuid() === 0;
let privCache: string[] | null | undefined;
/** The command prefix that may mount: [] as root, ['sudo', '-n'] with passwordless sudo, null when neither. */
export function mountPrivilege(): string[] | null {
  if (privCache === undefined) privCache = IS_ROOT ? [] : spawnSync('sudo', ['-n', 'true'], { stdio: 'ignore' }).status === 0 ? ['sudo', '-n'] : null;
  return privCache;
}
function privileged(cmd: string[]) {
  const pre = mountPrivilege() ?? [];
  const r = spawnSync([...pre, ...cmd][0], [...pre, ...cmd].slice(1), { encoding: 'utf8' });
  return { ok: r.status === 0, err: ((r.stderr || '') + (r.stdout || '') + (r.error ? String(r.error) : '')).trim() || 'exit ' + r.status };
}
function isMountPoint(dir: string) { return spawnSync('mountpoint', ['-q', dir]).status === 0; }

/** A small disk mounted at `dir` (root, or passwordless sudo: then owned by the current user); false when not allowed. */
export function smallDisk(dir: string, mb: number): boolean {
  fs.mkdirSync(dir, { recursive: true });
  if (mountPrivilege() === null) return false;
  let opts = 'size=' + mb + 'm';
  if (!IS_ROOT) opts += ',uid=' + process.getuid!() + ',gid=' + process.getgid!() + ',mode=' + (fs.statSync(dir).mode & 0o7777).toString(8).padStart(4, '0');
  return privileged(['mount', '-t', 'tmpfs', '-o', opts, 'tmpfs', dir]).ok;
}
/** A read-only bind mount of `dir` over itself; 'OK' or the reason it could not be made. */
export function readOnlyBind(dir: string): string {
  if (mountPrivilege() === null) return 'neither root nor passwordless sudo';
  const a = privileged(['mount', '--bind', dir, dir]); if (!a.ok) return a.err;
  const b = privileged(['mount', '-o', 'remount,bind,ro', dir]); if (!b.ok) { unmount(dir); return b.err; }
  return 'OK';
}
/** Unmounts every mount stacked at `dir` (lazily, so an open file cannot keep it). */
export function unmount(dir: string) {
  for (let i = 0; i < 5 && isMountPoint(dir); i++) privileged(['umount', '-l', dir]);
  if (!isMountPoint(dir)) return;
  try { execSync('umount -l "' + dir + '"', { stdio: 'pipe' }); } catch { }
}

/** The wrapper that runs a command in its own private mount namespace with `disk` seen at `at` (F14). As root: unshare
 *  as before. Not root with passwordless sudo: the namespace is made by root, then the command is run as the current
 *  user again (setpriv --reuid/--regid/--init-groups) — the product never runs as root. null when neither. */
export function privateView(disk: string, at: string): string[] | null {
  const pre = mountPrivilege(); if (pre === null || !hasProgram('unshare')) return null;
  if (IS_ROOT) return spawnSync('unshare', ['-m', 'true']).status === 0 ? ['unshare', '-m', '--', 'sh', '-c', 'mount --bind "$0" "$1" && shift && exec "$@"', disk, at] : null;
  if (!hasProgram('setpriv')) return null;
  const back = 'setpriv --reuid=' + process.getuid!() + ' --regid=' + process.getgid!() + ' --init-groups --';
  // sudo -E keeps the environment (the product's settings); PATH is given again since sudo replaces it (secure_path)
  const wrap = ['sudo', '-n', '-E', 'env', 'PATH=' + (process.env.PATH || ''), 'unshare', '-m', '--propagation', 'private', '--', 'sh', '-c', 'mount --bind "$0" "$1" && shift && exec ' + back + ' "$@"', disk, at];
  const probe = spawnSync(wrap[0], [...wrap.slice(1), 'sh', '-c', 'test "$(id -u)" = ' + process.getuid!()]);
  return probe.status === 0 ? wrap : null;
}

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
