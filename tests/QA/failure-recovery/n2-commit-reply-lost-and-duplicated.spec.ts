// N2 — The commit of a backup run (the request that makes the run a restore point) meets a bad line:
//   (a) the server commits, but its ANSWER is lost (the proxy forwards the request, swallows the reply, closes the line)
//   (b) the commit request arrives TWICE at the same time (the proxy sends a copy on a connection of its own)
// Both on run 2, which changes, adds and deletes files (so a commit applied twice would move/delete twice).
// Contracts: ST-01 "repeated commit answered the same"; AG-05 "a repeated request is not counted twice".
// Oracle: the proxy's log (the fault happened), the server's run history (each run once), the points (one per run),
// SHA-256 of point 1 and of the newest point.
import { test, expect } from '../lib/fixtures';
import { goldenDataset, manifest, compare, restoredPath, World, Agent } from '../lib/world';
import { Line, result, setRuns, points, keep } from '../lib/nfault';
import * as fs from 'fs';
import * as path from 'path';

async function twoRuns(world: World, login: string, arm: (line: Line) => Promise<unknown>) {
  const line = await new Line(world).start();
  world.addCustomer(login);
  const ag = world.agent(login, login.toUpperCase() + '-PC'); ag.register(line.url);
  const src = path.join(world.dir, 'data'); const v1 = goldenDataset(src);
  const id = ag.addSet('Files', [src]);
  const b1 = ag.backup(id); expect(b1.out, b1.out).toMatch(/^BS_STOP_SUCCESS /m);
  const p1 = points(ag, id); expect(p1.length).toBe(1);
  // run 2: a changed file, a new file, 5 deleted files
  fs.writeFileSync(path.join(src, 'Documents/letter.txt'), 'Dear customer,\nchanged before run 2.\n');
  fs.writeFileSync(path.join(src, 'Documents/new-in-run-2.txt'), 'new in run 2\n');
  for (let i = 0; i < 5; i++) fs.rmSync(path.join(src, 'Many/file-' + String(i).padStart(3, '0') + '.csv'));
  const v2 = manifest(src);
  await arm(line);
  const b2 = ag.backup(id);
  return { line, ag, id, src, v1, v2, p1: p1[0], b2 };
}

async function oracle(world: World, ag: Agent, login: string, id: string, src: string, v1: any, v2: any, p1: string, b2: { code: number, out: string }, evidence: any, line: Line) {
  const runs = await setRuns(world, login, id);
  evidence.step('server runs: ' + JSON.stringify(runs.map((r) => [r.job, r.result, r.new, r.upd, r.del])));
  const jobs = runs.map((r) => r.job);
  expect(new Set(jobs).size, 'each run recorded ONCE by the server: ' + jobs.join(',')).toBe(jobs.length);
  const pts = points(ag, id);
  evidence.step('points: ' + pts.join(','));
  if (b2.code === 0) {
    expect(runs.length, 'two runs → two records').toBe(2);
    expect(pts.length, 'two runs → two points').toBe(2);
    expect(runs[0].result, 'the server and the computer agree on run 2').toBe(result(b2.out));
    expect(Number(runs[0].del), 'the deletions of run 2 counted once').toBe(5);
  } else {
    expect(result(b2.out)).not.toMatch(/^BS_STOP_SUCCESS/);
  }
  evidence.step('RECOVERY: run 3 with no change');
  const b3 = ag.backup(id); expect(b3.out, b3.out).toMatch(/^BS_STOP_SUCCESS /m);
  evidence.step('run 3: ' + b3.out.split('\n')[0]);
  if (b2.code === 0) expect(b3.out, 'run 2 counted as committed on the computer too: run 3 has nothing to send').toMatch(/new=0 upd=0 perm=0 del=0/);
  const jobs3 = (await setRuns(world, login, id)).map((r) => r.job); expect(new Set(jobs3).size).toBe(jobs3.length);
  evidence.step('ORACLE: newest point = the files now; point 1 = the original');
  const tn = path.join(world.dir, 'restore-new'); const rn = ag.restore(id, tn); expect(rn.code, rn.out).toBe(0);
  expect(compare(v2, manifest(restoredPath(tn, src)))).toEqual([]);
  const t1 = path.join(world.dir, 'restore-1'); const r1 = ag.restore(id, t1, ['--point', p1]); expect(r1.code, r1.out).toBe(0);
  expect(compare(v1, manifest(restoredPath(t1, src)))).toEqual([]);
}

test('N2a the commit reply is lost after the server committed → not recorded twice, no file lost → run 3 sends nothing → both points identical', async ({ world, evidence }) => {
  const { line, ag, id, src, v1, v2, p1, b2 } = await twoRuns(world, 'qa-n2a', (l) => l.ctl('/drop-reply'));
  try {
    evidence.step('run 2: exit ' + b2.code + '\n' + b2.out.trim());
    // PROOF: the proxy forwarded run 2's commit, swallowed the server's reply and closed the computer's connection
    const ev = line.events();
    const dropped = ev.filter((e) => e.ev === 'reply-dropped');
    expect(dropped.length, 'the server answered and the answer was dropped: ' + JSON.stringify(ev.slice(-8))).toBeGreaterThan(0);
    expect(dropped[0].head, 'what was dropped is the server\'s HTTP reply').toMatch(/^HTTP\/1\.1 200/);
    expect(ev.some((e) => e.ev === 'client-connection-closed-without-reply')).toBe(true);
    const commits = ev.filter((e) => e.ev === 'commit-request');
    evidence.step('commit requests through the line: ' + commits.map((c) => c.path + ' [' + c.action + ']').join(' | '));
    keep('n2a', { b2: { code: b2.code, out: b2.out }, proxy: ev.filter((e) => e.ev !== 'rate') });
    await oracle(world, ag, 'qa-n2a', id, src, v1, v2, p1, b2, evidence, line);
  } finally { line.stop(); }
});

test('N2b the commit request arrives twice at once → answered the same, recorded once → run 3 sends nothing → both points identical', async ({ world, evidence }) => {
  const { line, ag, id, src, v1, v2, p1, b2 } = await twoRuns(world, 'qa-n2b', (l) => l.ctl('/dup-commit'));
  try {
    evidence.step('run 2: exit ' + b2.code + '\n' + b2.out.trim());
    const ev = line.events();
    expect(ev.some((e) => e.ev === 'dup-sent'), 'PROOF: the copy of the commit was sent: ' + JSON.stringify(ev.slice(-6))).toBe(true);
    const st = await line.stats();
    expect(st.dupReplies.length, 'the server answered the copy').toBe(1);
    evidence.step('the server\'s answer to the copy: ' + JSON.stringify(st.dupReplies[0]).slice(0, 700));
    keep('n2b', { b2: { code: b2.code, out: b2.out }, dupReplies: st.dupReplies, proxy: ev });
    await oracle(world, ag, 'qa-n2b', id, src, v1, v2, p1, b2, evidence, line);
  } finally { line.stop(); }
});
