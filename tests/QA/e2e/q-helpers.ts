// Round Q helpers (night round Q): small, outside-the-product oracles shared by the q-*.spec.ts files.
import { Page, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { Manifest } from '../lib/world';

/** The server's time format for "now" in the maintenance call (yyyy-MM-dd-HH-mm-ss, UTC). */
export function runId(d: Date) { return d.toISOString().replace(/[-T:]/g, '-').slice(0, 19); }

/** A plain listing of a folder on disk: relative path → size (no hashing — for "is it there / is it gone"). */
export function tree(root: string): Map<string, number> {
  const m = new Map<string, number>();
  const walk = (d: string) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const f = path.join(d, e.name); if (e.isDirectory()) walk(f); else if (e.isFile()) m.set(path.relative(root, f).split(path.sep).join('/'), fs.statSync(f).size); } };
  if (fs.existsSync(root)) walk(root);
  return m;
}

/** Every file under root whose relative path matches. */
export function find(root: string, re: RegExp): string[] { return [...tree(root).keys()].filter((k) => re.test(k)).map((k) => path.join(root, k)); }

/** A manifest without some entries (what a restore must give when those files are left out on purpose). */
export function without(m: Manifest, drop: (k: string) => boolean): Manifest { return new Map([...m].filter(([k]) => !drop(k))); }

/** The admin site's confirm dialog: press "Yes". */
export async function confirmYes(page: Page) { const d = page.locator('dialog[open]'); await expect(d).toBeVisible(); await d.getByRole('button', { name: 'Yes', exact: true }).click(); }

export async function rail(page: Page, k: string) { await page.locator('.rail button[data-k="' + k + '"]').click(); }

/** Waits for a condition checked outside the product (a file, a log line), polling. */
export async function until<T>(what: string, fn: () => T | undefined | false | Promise<T | undefined | false>, ms = 180000, every = 2000): Promise<T> {
  const end = Date.now() + ms; let last: unknown;
  while (Date.now() < end) { try { const v = await fn(); if (v) return v as T; } catch (e) { last = e; } await new Promise((r) => setTimeout(r, every)); }
  throw new Error('timeout (' + ms / 1000 + ' s) waiting for: ' + what + (last ? ' — last error: ' + last : ''));
}
