// F13 — The computer's clock is 3 hours AHEAD of the backup server's (a wrong time zone setting, a dead CMOS battery,
// a clock moved forward by hand). The set is scheduled once a day.
// Expected: the scheduled backup runs once for its slot — not again and again every 15 minutes because the server's run
// time is "older" than the computer's slot — and the backup restores identical.
// The computer's clock is moved with libfaketime (only the agent process; the server keeps the real time).
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath } from '../lib/world';
import { agentProcess, hasProgram } from '../lib/fault';
import { ChildProcess } from 'child_process';
import * as path from 'path';

test('F13 client clock 3 h ahead of the server → the daily slot runs once, not again every 15 minutes → restore identical', async ({ world, evidence }) => {
  test.skip(!hasProgram('faketime'), 'NOT TESTED: libfaketime (faketime) is not installed — the agent has no clock offset of its own');
  world.addCustomer('qa-f13');
  const ag = world.agent('qa-f13', 'F13-PC'); ag.register();
  const src = path.join(world.dir, 'data'); const data = goldenDataset(src);
  const AHEAD = 3 * 3600 * 1000;
  // the computer's "now" is the real time + 3 h; its slot one minute before that (due at once)
  const slot = new Date(Date.now() + AHEAD - 60000);
  const id = ag.addSet('Files', [src], ['--hour', String(slot.getHours()), '--minute', String(slot.getMinutes())]);
  const backups = async () => (await world.runs('qa-f13')).filter((r) => r.set === id && r.kind === 'Backup');

  let svc: ChildProcess | undefined;
  const runService = async (offset: string, waitForRuns: number, ms: number) => {
    svc = agentProcess(ag, ['service'], ['faketime', '-f', offset]);
    const until = Date.now() + ms;
    while (Date.now() < until && (await backups()).length < waitForRuns) await new Promise((r) => setTimeout(r, 3000));
    svc.kill('SIGTERM'); await new Promise((r) => svc!.on('exit', r)); svc = undefined;
  };
  try {
    evidence.step('the service runs with the computer clock at +3 h: the slot is due');
    await runService('+3h', 1, 180000);
    expect((await backups()).length, 'the scheduled backup ran').toBe(1);
    expect((await backups())[0].status).toBe('ok');
    evidence.step('20 minutes later (computer time): the service starts again — the slot was already backed up');
    await runService('+200m', 2, 90000);
    evidence.step('40 minutes later (computer time): again');
    await runService('+220m', 3, 90000);
    const all = await backups();
    evidence.step('backups of the set on the server: ' + all.length + ' ' + JSON.stringify(all.map((r) => r.job)));
    expect(all.length, 'one daily slot must give one scheduled backup, not one every 15 minutes (the computer\'s clock is ahead of the server\'s)').toBe(1);
  } finally { svc?.kill('SIGKILL'); }

  evidence.step('ORACLE: restore → identical');
  const target = path.join(world.dir, 'restore');
  const r = ag.restore(id, target); expect(r.code, r.out).toBe(0);
  expect(compare(data, manifest(restoredPath(target, src)))).toEqual([]);
});
