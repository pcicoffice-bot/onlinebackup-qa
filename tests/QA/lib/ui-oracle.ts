// Outside oracles for the admin-site journeys (ui-*.spec.ts): what the SERVER keeps, read without the page.
// - storedSet(): the set as the server's API returns it (GET /api/admin/users/<login>/sets/<id>)
// - diskSet():   the set as it is written in the customer's Profile.xml on the server's disk (the file the agent syncs from)
// Both are parsed by a small XML reader here — never by the page's own code.
import * as fs from 'fs';
import * as path from 'path';
import { Page, Locator, expect } from '@playwright/test';
import { World } from './world';

export type El = { tag: string, attrs: Record<string, string>, text: string, kids: El[] };

const unesc = (s: string) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&#x([0-9a-fA-F]+);/g, (_, h) => String.fromCodePoint(parseInt(h, 16))).replace(/&#(\d+);/g, (_, d) => String.fromCodePoint(Number(d))).replace(/&amp;/g, '&');

/** A minimal XML reader (elements, attributes, text) — enough for Profile.xml / a BACKUP_SET. */
export function parseXml(xml: string): El {
  const re = /<\?[\s\S]*?\?>|<!--[\s\S]*?-->|<\/([^\s>]+)\s*>|<([^\s/>]+)((?:\s+[^\s=]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>|([^<]+)/g;
  const root: El = { tag: '#root', attrs: {}, text: '', kids: [] }; const stack = [root]; let m: RegExpExecArray | null;
  while ((m = re.exec(xml))) {
    if (m[1]) { stack.pop(); continue; }
    if (m[2]) {
      const attrs: Record<string, string> = {};
      for (const a of m[3].matchAll(/([^\s=]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g)) attrs[a[1]] = unesc(a[2] ?? a[3] ?? '');
      const el: El = { tag: m[2], attrs, text: '', kids: [] }; stack[stack.length - 1].kids.push(el);
      if (!m[4]) stack.push(el);
      continue;
    }
    if (m[5] && stack.length > 1) stack[stack.length - 1].text += unesc(m[5]);
  }
  return root.kids[0];
}
export const kids = (e: El, tag: string) => e.kids.filter((k) => k.tag === tag);
export const kid = (e: El, tag: string) => kids(e, tag)[0];
const findSet = (e: El, id: string): El | undefined => e.tag === 'BACKUP_SET' && e.attrs.ID === id ? e : e.kids.map((k) => findSet(k, id)).find(Boolean);

/** The set as the server's API returns it. */
export async function storedSet(world: World, login: string, id: string): Promise<El> {
  const r = await world.adminApi('GET', 'users/' + login + '/sets/' + id);
  expect(r.status, r.text).toBe(200);
  const inner = /<f n="set">([\s\S]*?)<\/f>/.exec(r.text)![1];
  return parseXml(unesc(inner));
}
/** The set as written in the customer's Profile.xml on the server's disk. */
export function diskSet(world: World, login: string, id: string): El {
  let f = path.join(world.usersDir, login, 'db', 'Profile.xml');
  if (!fs.existsSync(f)) {   // the user home may keep the customer one level deeper
    const hit = fs.readdirSync(world.usersDir, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => path.join(world.usersDir, d.name, login, 'db', 'Profile.xml')).find((x) => fs.existsSync(x));
    if (!hit) throw new Error('no Profile.xml of ' + login + ' under ' + world.usersDir); f = hit;
  }
  const s = findSet(parseXml(fs.readFileSync(f, 'utf8').replace(/^﻿/, '')), id);
  if (!s) throw new Error('set ' + id + ' not in ' + f);
  return s;
}
/** The schedules of a set as plain values: days (S M T W T F S, '-' off), hour, minute, duration. */
export function schedules(set: El) {
  const D = ['SUN', 'MON', 'TUE', 'WED', 'THU', 'FRI', 'SAT'];
  return set.kids.filter((k) => k.tag === 'DAILY_SCHEDULE' || k.tag === 'WEEKLY_SCHEDULE').map((k) => ({
    days: k.tag === 'DAILY_SCHEDULE' ? 'SMTWTFS' : D.map((d, i) => (k.attrs[d] === 'Y' ? 'SMTWTFS'[i] : '-')).join(''),
    hour: k.attrs.HOUR, minute: k.attrs.MINUTE, duration: k.attrs.DURATION,
  }));
}

// ------------------------------------------------------------------ the set editor, as a person uses it
export const row = (page: Page, label: string | RegExp) => page.locator('.form .fr').filter({ has: page.locator('.lb b', { hasText: label }) }).first();
export async function toggle(r: Locator, on: boolean) {
  const i = r.locator('label.tog input[type=checkbox]').first();
  if ((await i.isChecked()) !== on) await i.click();
  await expect(i).toBeChecked({ checked: on });
}
export async function editorTab(page: Page, label: string) {
  await page.locator('.tabs[role=tablist]').last().locator('button', { hasText: label }).click();
  await expect(page.locator('.tabs[role=tablist]').last().locator('button.on')).toHaveText(label);
}
/** Console errors and failed requests seen by the page (the fixtures' Evidence), minus nothing. */
export function problems(e: { console: string[], network: string[] }) { return [...e.console.map((c) => 'console: ' + c), ...e.network.map((n) => 'network: ' + n)]; }
