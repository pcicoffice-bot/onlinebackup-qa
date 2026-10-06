#!/usr/bin/env node
// OnlineBackup QA runner — one command for everything:
//   node tests/QA/runner/qa.mjs [fast|candidate|nightly]      (default: candidate)
//     fast       build + unit + integration + API + regression (every commit)
//     candidate  everything of fast + full xUnit + QA journeys + failure/recovery (a version that may ship)
//     nightly    candidate + soak (long run)
// Writes tests/QA/reports/QA-REPORT.md and QA-REPORT.json with the count per level and the release verdict
// READY / NOT READY with its reasons. A gate item that was not run is NOT TESTED — never PASS.
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

const QA = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const REPO = path.resolve(QA, '../..');
const mode = process.argv[2] || 'candidate';
const levels = JSON.parse(fs.readFileSync(path.join(QA, 'levels.json'), 'utf8'));
const out = path.join(QA, 'reports'); fs.mkdirSync(out, { recursive: true });
const started = new Date();
const sh = (cmd, args, opts = {}) => { const t = Date.now(); const r = spawnSync(cmd, args, { cwd: REPO, encoding: 'utf8', maxBuffer: 1 << 28, ...opts }); return { code: r.status, out: (r.stdout || '') + (r.stderr || ''), secs: Math.round((Date.now() - t) / 1000) }; };
const commit = sh('git', ['rev-parse', '--short', 'HEAD']).out.trim();
const version = fs.existsSync(path.join(REPO, 'version.txt')) ? fs.readFileSync(path.join(REPO, 'version.txt'), 'utf8').trim() : (process.env.QA_VERSION || 'dev');
const R = { mode, commit, version, environment: process.platform + ' ' + process.arch + ', node ' + process.version, started: started.toISOString(), build: {}, levels: {}, items: {}, failures: [], notTested: [] };
const lv = (L) => (R.levels[L] ||= { name: levels.names[L], total: 0, passed: 0, failed: 0, skipped: 0, failedTests: [] });
const log = (s) => { process.stdout.write(s + '\n'); };

// 1. build every component
log('== build');
for (const [name, p, extra] of [['Core', 'src/Core', []], ['Server', 'src/Server', []], ['Agent (net40 + net8)', 'src/Agent', []], ['ClientApp', 'src/ClientApp', []], ['Setup', 'src/Setup', []], ['Tests', 'tests/Tests', []]]) {
  const r = sh('dotnet', ['build', p, '-nologo', '-v', 'q', ...extra]);
  R.build[name] = r.code === 0 ? 'PASS' : 'FAIL';
  if (r.code !== 0) R.failures.push({ what: 'build ' + name, detail: r.out.split('\n').filter((l) => / error /.test(l)).slice(0, 10).join('\n') });
  log('  ' + name + ': ' + R.build[name] + ' (' + r.secs + ' s)');
}
R.items.build = Object.values(R.build).every((x) => x === 'PASS') ? 'PASS' : 'FAIL';

// 2. xUnit (unit, integration, API, E2E, failure/recovery, install/upgrade levels)
log('== xUnit');
const trxDir = path.join(out, 'trx'); fs.rmSync(trxDir, { recursive: true, force: true });
const filter = mode === 'fast' ? ['--filter', Object.entries(levels.xunit).filter(([, l]) => ['L1', 'L2', 'L3', 'L6'].includes(l)).map(([c]) => 'FullyQualifiedName~.' + c + '.').join('|')] : [];
const xr = sh('dotnet', ['test', 'tests/Tests', '--no-build', '-nologo', '--logger', 'trx;LogFileName=xunit.trx', '--results-directory', trxDir, ...filter], { timeout: 3 * 3600 * 1000 });
log('  ' + (xr.out.match(/(Passed|Failed)!.*$/m) || ['(no summary)'])[0]);
const trx = path.join(trxDir, 'xunit.trx');
if (fs.existsSync(trx)) {
  const x = fs.readFileSync(trx, 'utf8');
  for (const m of x.matchAll(/<UnitTestResult [^>]*testName="([^"]+)"[^>]*outcome="([^"]+)"/g)) {
    const full = m[1].replace(/&quot;/g, '"'); const cls = (full.match(/OnlineBackup\.Tests\.([A-Za-z0-9]+)\./) || [])[1] || '?';
    const L = levels.xunit[cls] || 'L2'; const s = lv(L); s.total++;
    if (m[2] === 'Passed') s.passed++; else if (m[2] === 'Failed') { s.failed++; s.failedTests.push(full); R.failures.push({ what: levels.names[L] + ': ' + full, detail: '' }); } else s.skipped++;
  }
} else R.failures.push({ what: 'xUnit did not run', detail: xr.out.slice(-3000) });

// 3. the black-box QA journeys (real server, real agent, browser, SHA-256 oracle)
if (mode !== 'fast') {
  log('== QA journeys and failure/recovery (Playwright)');
  const pr = sh('npx', ['playwright', 'test', '--reporter=json'], { cwd: QA, env: { ...process.env, QA_NO_BUILD: '1', QA_COMMIT: commit }, timeout: 4 * 3600 * 1000 });
  let j = null; try { j = JSON.parse(pr.out.slice(pr.out.indexOf('{'))); } catch { R.failures.push({ what: 'QA journeys: no report', detail: pr.out.slice(-3000) }); }
  const walk = (suite, file) => {
    for (const s of suite.suites || []) walk(s, s.file || file);
    for (const sp of suite.specs || []) {
      const f = (sp.file || file || '').replace(/\\/g, '/');
      const folder = f.split('/')[0]; const L = levels.qa[folder] || 'L5'; const st = lv(L); st.total++;
      const ok = sp.ok && sp.tests.every((t) => t.results.every((x) => x.status === 'passed'));
      const id = 'qa:' + f.replace(/\.spec\.ts$/, '');
      R.items[id] = ok ? 'PASS' : 'FAIL';
      if (ok) st.passed++; else { st.failed++; st.failedTests.push(f + ' › ' + sp.title); R.failures.push({ what: 'QA ' + f + ' › ' + sp.title, detail: (sp.tests[0]?.results[0]?.error?.message || '').slice(0, 1500) }); }
    }
  };
  if (j) for (const s of j.suites || []) walk(s, s.file);
}

// 4. real Windows results (the Windows CI job writes reports/windows.json); absent = NOT TESTED
const win = path.join(out, 'windows.json');
const W = fs.existsSync(win) ? JSON.parse(fs.readFileSync(win, 'utf8')) : {};
for (const k of ['install', 'update', 'reboot']) R.items['windows:' + k] = W[k] || 'NOT TESTED';

// 5. the release gate
const levelOk = (L) => R.levels[L] && R.levels[L].total > 0 ? (R.levels[L].failed === 0 ? 'PASS' : 'FAIL') : 'NOT TESTED';
const gate = levels.gate.map((g) => {
  let st;
  if (g.id === 'build') st = R.items.build;
  else if (/^L\d$/.test(g.id)) st = levelOk(g.id);
  else if (g.id === 'qa:failure-recovery') { const ks = Object.keys(R.items).filter((k) => k.startsWith('qa:failure-recovery/')); st = !ks.length ? 'NOT TESTED' : ks.every((k) => R.items[k] === 'PASS') ? 'PASS' : 'FAIL'; }
  else { const k = Object.keys(R.items).find((x) => x === g.id || x.startsWith(g.id + '-')); st = k ? R.items[k] : 'NOT TESTED'; }
  return { ...g, status: st };
});
R.gate = gate;
R.verdict = gate.every((g) => g.status === 'PASS') ? 'READY' : 'NOT READY';
R.reasons = gate.filter((g) => g.status !== 'PASS').map((g) => g.what + ': ' + g.status);
const nt = path.join(QA, 'NOT-TESTED.md'); R.notTested = fs.existsSync(nt) ? fs.readFileSync(nt, 'utf8') : '';
R.finished = new Date().toISOString(); R.minutes = Math.round((Date.now() - started) / 60000);
fs.writeFileSync(path.join(out, 'QA-REPORT.json'), JSON.stringify(R, null, 2));

// 6. the report a person reads
const row = (L) => { const s = R.levels[L]; return '| ' + L + ' ' + levels.names[L] + ' | ' + (s ? s.total : 0) + ' | ' + (s ? s.passed : 0) + ' | ' + (s ? s.failed : 0) + ' | ' + (s ? (s.total ? (s.failed ? '**FAIL**' : 'PASS') : 'NOT TESTED') : 'NOT TESTED') + ' |'; };
const md = [
  '# OnlineBackup — QA report', '',
  '| | |', '|---|---|', '| Version | ' + version + ' |', '| Commit | ' + commit + ' |', '| Mode | ' + mode + ' |', '| Environment | ' + R.environment + ' |',
  '| Start / end | ' + R.started + ' → ' + R.finished + ' (' + R.minutes + ' min) |', '', '## Verdict: **' + R.verdict + '**', '',
  ...(R.reasons.length ? ['Reasons:', '', ...R.reasons.map((r) => '- ' + r), ''] : []),
  '## Build', '', ...Object.entries(R.build).map(([k, v]) => '- ' + k + ': ' + v), '',
  '## Tests by level', '', '| Level | Tests | Passed | Failed | Result |', '|---|---|---|---|---|', ...['L1', 'L2', 'L3', 'L4', 'L5', 'L6', 'L7', 'L8'].map(row), '',
  '## Release gate', '', '| Item | Result |', '|---|---|', ...gate.map((g) => '| ' + g.what + ' | ' + (g.status === 'PASS' ? 'PASS' : '**' + g.status + '**') + ' |'), '',
  '## Failures', '', ...(R.failures.length ? R.failures.map((f) => '- **' + f.what + '**' + (f.detail ? '\n  ```\n  ' + f.detail.replace(/\n/g, '\n  ') + '\n  ```' : '')) : ['(none)']), '',
  'Evidence of each failure (screenshot, video, trace, console, network, server and agent logs): `tests/QA/reports/bugs/` and `tests/QA/artifacts/`.', '',
  '## NOT TESTED', '', R.notTested || '(see tests/QA/NOT-TESTED.md)', '',
].join('\n');
fs.writeFileSync(path.join(out, 'QA-REPORT.md'), md);
log('\n' + R.verdict + (R.reasons.length ? ':\n  - ' + R.reasons.join('\n  - ') : ''));
process.exit(R.verdict === 'READY' ? 0 : 1);
