// The network line between a computer and the server, as its own process (so it keeps working while the test waits):
//   node netproxy.mjs <target-port>   → prints "listening <port>"; SIGUSR1 = cut the line, SIGUSR2 = line back
import * as net from 'net';
const target = Number(process.argv[2]); let down = false; const socks = new Set();
const srv = net.createServer((c) => {
  if (down) { c.destroy(); return; }
  const u = net.connect({ port: target, host: 'localhost', autoSelectFamily: true }); socks.add(c); socks.add(u);   // the server may listen on ::1 only
  c.pipe(u); u.pipe(c);
  const end = () => { c.destroy(); u.destroy(); socks.delete(c); socks.delete(u); };
  c.on('error', end); u.on('error', end); c.on('close', end); u.on('close', end);
});
srv.listen(0, () => console.log('listening ' + srv.address().port));   // every address, IPv4 and IPv6
process.on('SIGUSR1', () => { down = true; for (const s of socks) s.destroy(); socks.clear(); console.log('cut'); });
process.on('SIGUSR2', () => { down = false; console.log('back'); });
