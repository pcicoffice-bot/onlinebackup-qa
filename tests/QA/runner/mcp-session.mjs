// A persistent MCP session with one of the project's MCP servers (.mcp.json): reads one JSON command per line
// ({"tool": "...", "args": {...}}) from standard input, writes each result to standard output. Used to drive the
// official Playwright MCP servers outside an interactive Claude Code session (exploratory runs, CI).
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as readline from 'readline';
const name = process.argv[2] || 'playwright-test';
const cfg = JSON.parse(fs.readFileSync('.mcp.json', 'utf8')).mcpServers[name];
const p = spawn(cfg.command, cfg.args, { stdio: ['pipe', 'pipe', 'inherit'] });
let buf = ''; const wait = {}; let id = 0;
p.stdout.on('data', (d) => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const l = buf.slice(0, i); buf = buf.slice(i + 1); try { const m = JSON.parse(l); if (m.id && wait[m.id]) { wait[m.id](m); delete wait[m.id]; } } catch {} } });
const call = (method, params) => new Promise((r) => { const n = ++id; wait[n] = r; p.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n'); });
await call('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'qa-session', version: '1' } });
p.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
console.log('READY ' + name);
const rl = readline.createInterface({ input: process.stdin });
for await (const line of rl) {
  if (!line.trim()) continue;
  const c = JSON.parse(line);
  const r = await call('tools/call', { name: c.tool, arguments: c.args || {} });
  const text = (r.result?.content || []).map((x) => x.type === 'text' ? x.text : '[' + x.type + ']').join('\n') || JSON.stringify(r.error || r.result);
  console.log('=== ' + c.tool + (r.result?.isError ? ' (ERROR)' : '') + '\n' + text + '\n=== END');
}
p.kill();
