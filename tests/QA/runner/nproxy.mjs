// Agent N (failure matrix): a fault-injecting TCP proxy between the computer and the server, as its own process, with
// a log of every fault it really applied (the evidence that the fault happened, from outside the product).
//   node nproxy.mjs <target-port> <log-file>   → prints "listening <port> control <port>"
// Control (HTTP on the control port, from the test):
//   POST /cut            every connection dropped, new ones refused (the line is down)
//   POST /back           the line is back
//   POST /rate?bps=N     every connection limited to N bytes/s in each direction (0 = no limit)
//   POST /drop-reply     the NEXT commit request is forwarded to the server, the server's reply is swallowed and the
//                        computer's connection closed (the answer of a commit is lost after the server committed)
//   POST /dup-commit     the NEXT commit request is also sent a second time, at once, on a connection of its own
//   GET  /stats          the counters (JSON)
// Log: one JSON line per event in <log-file>.
import * as net from 'net';
import * as http from 'http';
import * as fs from 'fs';

const target = Number(process.argv[2]); const logf = process.argv[3];
const st = { down: false, rate: 0, dropReply: 0, dupCommit: 0, conns: 0, refused: 0, cutSockets: 0, c2s: 0, s2c: 0, c2sWhileThrottled: 0,
  commits: [], droppedReplyBytes: 0, droppedReplyHead: '', dupReplies: [] };
const log = (ev, o = {}) => fs.appendFileSync(logf, JSON.stringify({ t: new Date().toISOString(), ev, ...o }) + '\n');
const socks = new Set(); let ids = 0;
const COMMIT = /POST (\/api\/sets\/[^/ ]+\/jobs\/[^/ ]+\/commit[^ ]*) HTTP\/1\.1\r\n/g;

function inspect(conn, d) {
  // the commit request: found in the computer's bytes, captured whole (headers + Content-Length body)
  conn.acc = Buffer.concat([conn.acc, d]);
  for (;;) {
    if (!conn.cap) {
      const s = conn.acc.toString('latin1'); COMMIT.lastIndex = 0; const m = COMMIT.exec(s);
      if (!m) { if (conn.acc.length > 8192) conn.acc = conn.acc.subarray(conn.acc.length - 4096); return; }
      conn.cap = { path: m[1] }; conn.acc = conn.acc.subarray(m.index);
    }
    const s = conn.acc.toString('latin1'); const he = s.indexOf('\r\n\r\n'); if (he < 0) return;
    const cl = /\r\ncontent-length: *(\d+)/i.exec(s.slice(0, he)); const len = cl ? Number(cl[1]) : 0;
    if (conn.acc.length < he + 4 + len) return;
    const req = Buffer.from(conn.acc.subarray(0, he + 4 + len)); conn.acc = conn.acc.subarray(he + 4 + len);
    const path = conn.cap.path; conn.cap = null;
    const rec = { conn: conn.id, path, bytes: req.length, at: new Date().toISOString(), action: 'forwarded' };
    if (st.dropReply > 0) { st.dropReply--; conn.swallow = true; rec.action = 'forwarded, reply will be dropped'; }
    if (st.dupCommit > 0) { st.dupCommit--; rec.action += ' + duplicated'; duplicate(req, path); }
    st.commits.push(rec); log('commit-request', rec);
  }
}

function duplicate(req, path) {
  const u = net.connect({ port: target, host: 'localhost', autoSelectFamily: true });
  let got = Buffer.alloc(0);
  u.on('connect', () => { u.write(req); log('dup-sent', { path, bytes: req.length }); });
  u.on('data', (d) => {
    got = Buffer.concat([got, d]); const s = got.toString('utf8'); const he = s.indexOf('\r\n\r\n');
    if (he < 0) return;
    const cl = /\r\ncontent-length: *(\d+)/i.exec(s.slice(0, he));
    if (cl && got.length < he + 4 + Number(cl[1])) return;
    const r = { path, status: s.split('\r\n')[0], body: s.slice(he + 4, he + 4 + 600) };
    st.dupReplies.push(r); log('dup-reply', r); u.destroy();
  });
  u.on('error', (e) => { st.dupReplies.push({ path, error: String(e) }); log('dup-error', { path, error: String(e) }); });
}

function pipe(from, to, dir, conn) {
  from.on('data', (d) => {
    if (dir === 'c2s') { st.c2s += d.length; if (st.rate > 0) st.c2sWhileThrottled += d.length; inspect(conn, d); }
    else {
      st.s2c += d.length;
      if (conn.swallow) {
        st.droppedReplyBytes += d.length; if (!st.droppedReplyHead) st.droppedReplyHead = d.toString('latin1').slice(0, 300);
        log('reply-dropped', { conn: conn.id, bytes: d.length, head: d.toString('latin1').slice(0, 200) });
        if (!conn.closing) { conn.closing = true; setTimeout(() => { log('client-connection-closed-without-reply', { conn: conn.id }); conn.c.destroy(); conn.u.destroy(); }, 300); }
        return;
      }
    }
    if (st.rate > 0) { from.pause(); to.write(d); setTimeout(() => from.resume(), Math.ceil(d.length * 1000 / st.rate)); }
    else if (!to.write(d)) { from.pause(); to.once('drain', () => from.resume()); }
  });
}

const srv = net.createServer((c) => {
  if (st.down) { st.refused++; log('refused', { remote: c.remotePort }); c.destroy(); return; }
  const id = ++ids; st.conns++;
  const u = net.connect({ port: target, host: 'localhost', autoSelectFamily: true });   // the server may listen on ::1 only
  const conn = { id, c, u, acc: Buffer.alloc(0), cap: null, swallow: false, closing: false };
  socks.add(c); socks.add(u);
  pipe(c, u, 'c2s', conn); pipe(u, c, 's2c', conn);
  const end = () => { c.destroy(); u.destroy(); socks.delete(c); socks.delete(u); };
  c.on('error', end); u.on('error', end); c.on('close', end); u.on('close', end);
});

const ctl = http.createServer((q, r) => {
  const u = new URL(q.url, 'http://x');
  if (u.pathname === '/cut') { st.down = true; const n = socks.size; for (const s of socks) s.destroy(); socks.clear(); st.cutSockets += n; log('cut', { socketsDestroyed: n }); }
  else if (u.pathname === '/back') { st.down = false; log('back'); }
  else if (u.pathname === '/rate') { st.rate = Number(u.searchParams.get('bps') || 0); log('rate', { bps: st.rate }); }
  else if (u.pathname === '/drop-reply') { st.dropReply++; log('armed-drop-reply'); }
  else if (u.pathname === '/dup-commit') { st.dupCommit++; log('armed-dup-commit'); }
  r.writeHead(200, { 'Content-Type': 'application/json' }); r.end(JSON.stringify(st));
});

srv.listen(0, () => ctl.listen(0, '127.0.0.1', () => console.log('listening ' + srv.address().port + ' control ' + ctl.address().port)));
