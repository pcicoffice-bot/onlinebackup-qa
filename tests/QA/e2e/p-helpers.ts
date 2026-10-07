// Helpers of the P07+ journeys (additive: world.ts, fault.ts and nfault.ts are not changed).
// - OpenWorld: the same World, but the server listens on every address of this computer (prefix http://+:port/), so a
//   browser can reach it from a non-loopback address — the server then sees that address, as it sees a technician's
//   or an attacker's computer (the server trusts this computer itself: no lock, no code — SEC-090/110).
// - SmtpSink: a mail server of the test (the oracle of "the administrators are alerted": the mails really sent).
// - the agent service's own result lines, bytes on the server's disk, the admin site's sign-in form.
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as net from 'net';
import * as os from 'os';
import * as path from 'path';
import { Page, expect } from '@playwright/test';
import { World } from '../lib/world';
import { tree } from './q-helpers';

const SERVER_DLL = path.join(process.env.QA_PRODUCT ? path.resolve(process.env.QA_PRODUCT) : path.resolve(__dirname, '../../..'), 'src/Server/bin/Debug/net8.0/OnlineBackup.Server.dll');
const freePort = () => new Promise<number>((res) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as net.AddressInfo).port; s.close(() => res(p)); }); });

/** The World with a server listening on every address of this computer. `url` stays http://localhost:<port>. */
export class OpenWorld extends World {
  async startServer() {
    if (!this.url) this.url = 'http://localhost:' + (await freePort());
    const port = new URL(this.url).port;
    fs.appendFileSync(this.serverLog, '\n==== server start (every address) ' + new Date().toISOString() + '\n');
    const out = fs.openSync(this.serverLog, 'a');
    const mark = fs.statSync(this.serverLog).size;
    this.server = spawn('dotnet', [SERVER_DLL, 'run', '--system-home', this.sys, '--prefix', 'http://+:' + port + '/'], { env: { ...process.env, ...this.env }, stdio: ['ignore', out, out] });
    const until = Date.now() + 60000;
    while (Date.now() < until) { if (fs.readFileSync(this.serverLog, 'utf8').slice(mark).includes('listening')) return; await new Promise((r) => setTimeout(r, 200)); }
    throw new Error('the server did not start:\n' + fs.readFileSync(this.serverLog, 'utf8').slice(-3000));
  }
  /** The server's address as seen from this computer's own network card (undefined: there is none). */
  lanUrl() { const ip = lanAddress(); return ip ? 'http://' + ip + ':' + new URL(this.url).port : undefined; }
}

/** The first IPv4 address of this computer that is not a loopback address. */
export function lanAddress(): string | undefined {
  for (const list of Object.values(os.networkInterfaces())) for (const a of list || []) if (a.family === 'IPv4' && !a.internal && !a.address.startsWith('127.')) return a.address;
  return undefined;
}
/** The ranges the server's Guard never blocks by default (an office network): 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10. */
export function officeAddress(ip: string) {
  const b = ip.split('.').map(Number);
  return b[0] === 10 || (b[0] === 172 && b[1] >= 16 && b[1] <= 31) || (b[0] === 192 && b[1] === 168) || (b[0] === 169 && b[1] === 254) || (b[0] === 100 && b[1] >= 64 && b[1] <= 127);
}

// ------------------------------------------------------------------ a mail server of the test
export type Mail = { from: string, to: string[], subject: string, data: string };
/** RFC 2047 encoded words (=?utf-8?B?...?= / ?Q?) in a header value. */
function decodeWords(v: string) {
  return v.replace(/\?=\s+=\?/g, '?==?').replace(/=\?([^?]+)\?([BbQq])\?([^?]*)\?=/g, (_, cs, enc, text) => {
    const buf = /b/i.test(enc) ? Buffer.from(text, 'base64') : Buffer.from(text.replace(/_/g, ' ').replace(/=([0-9A-Fa-f]{2})/g, (_m: string, h: string) => String.fromCharCode(parseInt(h, 16))), 'binary');
    return buf.toString(/utf-?8/i.test(cs) ? 'utf8' : 'latin1');
  });
}
export class SmtpSink {
  port = 0; mails: Mail[] = []; private server?: net.Server;
  async start() {
    this.server = net.createServer((sock) => {
      sock.setEncoding('utf8');
      let buf = '', inData = false, cur = { from: '', to: [] as string[], data: '' };
      sock.write('220 qa-sink ESMTP\r\n');
      sock.on('error', () => {});
      sock.on('data', (d) => {
        buf += d;
        for (;;) {
          if (inData) {
            const i = buf.indexOf('\r\n.\r\n'); if (i < 0) return;
            cur.data += buf.slice(0, i); buf = buf.slice(i + 5); inData = false;
            const head = cur.data.split(/\r\n\r\n/)[0].replace(/\r\n[ \t]+/g, ' ');
            const subj = /^Subject:\s*(.*)$/mi.exec(head);
            this.mails.push({ ...cur, subject: decodeWords(subj ? subj[1].trim() : '') });
            cur = { from: '', to: [], data: '' }; sock.write('250 OK\r\n'); continue;
          }
          const i = buf.indexOf('\r\n'); if (i < 0) return;
          const line = buf.slice(0, i); buf = buf.slice(i + 2);
          const cmd = line.slice(0, 4).toUpperCase();
          if (cmd === 'EHLO') sock.write('250-qa-sink\r\n250 OK\r\n');
          else if (cmd === 'MAIL') { cur.from = line.replace(/^MAIL FROM:\s*/i, '').replace(/[<>]/g, ''); sock.write('250 OK\r\n'); }
          else if (cmd === 'RCPT') { cur.to.push(line.replace(/^RCPT TO:\s*/i, '').replace(/[<>]/g, '')); sock.write('250 OK\r\n'); }
          else if (cmd === 'DATA') { inData = true; sock.write('354 go on\r\n'); }
          else if (cmd === 'QUIT') { sock.write('221 bye\r\n'); sock.end(); return; }
          else sock.write('250 OK\r\n');
        }
      });
    });
    await new Promise<void>((r) => this.server!.listen(0, '127.0.0.1', () => r()));
    this.port = (this.server!.address() as net.AddressInfo).port;
    return this;
  }
  stop() { this.server?.close(); }
}

/** E-mails and alerts on the admin site: this test's mail server (no encryption), a sender, the IT company's contact. */
export async function mailSettings(page: Page, port: number, contact: string) {
  await page.locator('.rail button[data-k="notify"]').click();
  await expect(page.locator('main h1').first()).toHaveText('E-mails and alerts');
  const fr = (label: string) => page.locator('.form .fr').filter({ has: page.locator('.lb b', { hasText: label }) }).first();
  const smtp = fr('Mail servers').locator('.grid.g4').first();
  await smtp.locator('input').nth(0).fill('127.0.0.1');
  await smtp.locator('input').nth(1).fill(String(port));
  await smtp.locator('select').selectOption('NONE');
  await fr('Sender').locator('input').nth(0).fill('QA backup server');
  await fr('Sender').locator('input').nth(1).fill('backup@example.invalid');
  await fr('Contacts of the IT company').locator('textarea').fill(contact);
  await page.getByRole('button', { name: 'Save and exit' }).click();
  await expect(page.locator('#toast').filter({ hasText: 'Saved' })).toBeVisible();
}

// ------------------------------------------------------------------ the admin site's sign-in form
/** One sign-in through the form; returns the server's HTTP answer and the text the form shows (empty when signed in). */
export async function trySignIn(page: Page, base: string, login: string, password: string, otp: string) {
  if (!(await page.locator('form.login').isVisible().catch(() => false))) { await page.goto(base + '/admin'); await expect(page.locator('form.login')).toBeVisible(); }
  await page.locator('form.login input[autocomplete=username]').fill(login);
  await page.locator('form.login input[type=password]').fill(password);
  await page.locator('form.login input.otp').fill(otp);
  const [resp] = await Promise.all([page.waitForResponse((r) => r.url().endsWith('/api/admin/login') && r.request().method() === 'POST'), page.locator('form.login button[type=submit]').click()]);
  const status = resp.status();
  if (status === 200) return { status, shown: '' };
  const err = page.locator('form.login .loginerr');
  await expect(err).toBeVisible();
  return { status, shown: (await err.innerText()).trim() };
}

// ------------------------------------------------------------------ the agent service and the server's disk
/** The service's own result lines: "<local time> <set>: BS_STOP_..." (the agent's word, not the site's). */
export function serviceResults(log: string, set: string) {
  if (!fs.existsSync(log)) return [] as { at: string, result: string }[];
  const esc = set.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return [...fs.readFileSync(log, 'utf8').matchAll(new RegExp('^(\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d) ' + esc + ': (BS_[A-Z_]+)', 'gm'))].map((m) => ({ at: m[1], result: m[2] }));
}
/** Bytes stored under a folder of the server's disk. */
export const bytesOn = (dir: string) => [...tree(dir).values()].reduce((a, x) => a + x, 0);
