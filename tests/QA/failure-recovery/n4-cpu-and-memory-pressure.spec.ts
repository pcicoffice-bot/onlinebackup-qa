// N4 — The computer is under pressure during a backup:
//   CPU: one busy loop per core for the whole backup
//   memory: one process holding 40 % of the free memory (never more than 50 %: the machine is shared), its pages touched
// Expected: the backup is still correct, only slower — success, and every file restores identical (SHA-256).
// Three sets of the same size (golden dataset, different seeds) on one computer: baseline, CPU pressure, memory pressure;
// the durations are recorded (tests/QA/night/n/evidence/n4.json).
// The fault is proven from outside the product: the hogs' process state and CPU time (/proc/<pid>/stat), the load
// average, the memory hog's resident size (/proc/<pid>/status VmRSS) and MemAvailable before / during.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { backupAsync, result, setRuns, sleep, until, keep } from '../lib/nfault';
import { spawn, ChildProcess } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const meminfo = (k: string) => Number(new RegExp('^' + k + ':\\s+(\\d+) kB', 'm').exec(fs.readFileSync('/proc/meminfo', 'utf8'))![1]) * 1024;
const procStat = (pid: number) => { try { const f = fs.readFileSync('/proc/' + pid + '/stat', 'utf8').split(') ')[1].split(' '); return { state: f[0], cpuTicks: Number(f[11]) + Number(f[12]) }; } catch { return null; } };
const rss = (pid: number) => { try { return Number(/VmRSS:\s+(\d+) kB/.exec(fs.readFileSync('/proc/' + pid + '/status', 'utf8'))![1]) * 1024; } catch { return 0; } };

test('N4 CPU and memory pressure during backups → still success, only slower (durations recorded) → every set restores identical (SHA-256)', async ({ world, evidence }) => {
  test.setTimeout(20 * 60 * 1000);
  world.addCustomer('qa-n4', 20);
  const ag = world.agent('qa-n4', 'N4-PC'); ag.register();
  const sets: { name: string, src: string, data: any, id: string }[] = [];
  for (const [i, name] of ['baseline', 'cpu', 'memory'].entries()) {
    const src = path.join(world.dir, 'data-' + name); const data = goldenDataset(src, i + 1);
    sets.push({ name, src, data, id: ag.addSet(name, [src]) });
  }
  const ev: Record<string, unknown> = { cores: os.cpus().length };
  const hogs: ChildProcess[] = [];
  try {
    evidence.step('baseline backup (no pressure)');
    const b0 = await backupAsync(ag, sets[0].id).done;
    expect(b0.out, b0.out).toMatch(/^BS_STOP_SUCCESS /m);
    ev.baselineMs = b0.ms; ev.loadBefore = fs.readFileSync('/proc/loadavg', 'utf8').trim();

    evidence.step('CPU pressure: a busy loop on every core');
    for (let i = 0; i < os.cpus().length; i++) hogs.push(spawn(process.execPath, ['-e', 'for(;;){}'], { stdio: 'ignore' }));
    await sleep(3000);
    const before = hogs.map((h) => procStat(h.pid!));
    const run1 = backupAsync(ag, sets[1].id);
    const loads: string[] = [];
    const sampler = setInterval(() => loads.push(fs.readFileSync('/proc/loadavg', 'utf8').trim()), 5000);
    const b1 = await run1.done; clearInterval(sampler);
    const after = hogs.map((h) => procStat(h.pid!));
    for (const h of hogs) h.kill('SIGKILL'); hogs.length = 0;
    // PROOF: every hog was running (state R) and burned CPU during the backup
    ev.cpu = { ms: b1.ms, hogsBefore: before, hogsAfter: after, loads };
    evidence.step('cpu: ' + JSON.stringify(ev.cpu));
    for (let i = 0; i < after.length; i++) {
      expect(after[i], 'hog ' + i + ' alive until the end of the backup').not.toBeNull();
      expect(after[i]!.cpuTicks - before[i]!.cpuTicks, 'hog ' + i + ' burned CPU during the backup (ticks)').toBeGreaterThan(b1.ms / 10 * 0.3);
    }
    expect(b1.out, 'CPU pressure is not a failure:\n' + b1.out).toMatch(/^BS_STOP_SUCCESS /m);

    evidence.step('memory pressure: one process holds 40 % of the free memory');
    const free = meminfo('MemFree'), availBefore = meminfo('MemAvailable');
    const hold = Math.min(Math.floor(free * 0.4), 6 * 1024 * 1024 * 1024);
    const mb = Math.floor(hold / 1024 / 1024);
    const memHog = spawn(process.execPath, ['--max-old-space-size=64', '-e',
      'const a=[];for(let i=0;i<' + mb + ';i++){const b=Buffer.allocUnsafe(1048576);b.fill(i&255);a.push(b);}console.log("held");setInterval(()=>{let s=0;for(const b of a)s+=b[0];},5000);'], { stdio: ['ignore', 'pipe', 'inherit'] });
    hogs.push(memHog);
    const held = await new Promise<boolean>((r) => { memHog.stdout!.on('data', (d) => { if (String(d).includes('held')) r(true); }); memHog.on('exit', () => r(false)); });
    expect(held, 'the memory hog holds its memory').toBe(true);
    const hogRss = rss(memHog.pid!), availDuring = meminfo('MemAvailable');
    ev.memory = { freeBefore: free, availBefore, held: hold, hogRss, availDuring };
    evidence.step('memory: ' + JSON.stringify(ev.memory));
    expect(hogRss, 'PROOF: the hog\'s resident memory').toBeGreaterThan(hold * 0.9);
    expect(hold, 'never more than 50 % of the free memory').toBeLessThanOrEqual(free * 0.5);
    const b2 = await backupAsync(ag, sets[2].id).done;
    ev.memory = { ...(ev.memory as object), ms: b2.ms, hogRssAfter: rss(memHog.pid!) };
    memHog.kill('SIGKILL'); hogs.length = 0;
    expect(b2.out, 'memory pressure is not a failure:\n' + b2.out).toMatch(/^BS_STOP_SUCCESS /m);
    evidence.step('durations ms: baseline ' + b0.ms + ', cpu ' + b1.ms + ', memory ' + b2.ms);
  } finally { for (const h of hogs) h.kill('SIGKILL'); keep('n4', ev); }

  evidence.step('RECOVERY: a new backup of each set (a change) and restores');
  for (const s of sets) {
    fs.writeFileSync(path.join(s.src, 'Documents/after-pressure.txt'), 'after ' + s.name + '\n');
    const v = manifest(s.src);
    const b = ag.backup(s.id); expect(b.out, b.out).toMatch(/^BS_STOP_SUCCESS /m);
    const runs = await setRuns(world, 'qa-n4', s.id);
    expect(runs.map((r) => r.status), s.name + ': both runs ok on the server').toEqual(['ok', 'ok']);
    const t = path.join(world.dir, 'restore-' + s.name); const r = ag.restore(s.id, t); expect(r.code, r.out).toBe(0);
    expect(compare(v, manifest(restoredPath(t, s.src))), s.name).toEqual([]);
    const pts = ag.cli(['points', '--set', s.id]).out.trim().split('\n').filter((l) => /^\d+/.test(l)).map((l) => l.trim());
    const t1 = path.join(world.dir, 'restore-first-' + s.name); const r1 = ag.restore(s.id, t1, ['--point', pts[0]]); expect(r1.code, r1.out).toBe(0);
    expect(compare(s.data, manifest(restoredPath(t1, s.src))), s.name + ' point 1 (made under pressure)').toEqual([]);
  }
});
