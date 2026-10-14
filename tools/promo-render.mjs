// promo-render.mjs — 把宣传动画逐帧渲染出来（用 CDP，不装任何包）
//
// 页面加载一次并停在 t=0（?t=0 会暂停播放器），之后每帧只做两件事：
//   Runtime.evaluate('__seek(t)')  →  把整帧写成 t 的纯函数
//   Page.captureScreenshot          →  取回位图
// 所以导出的画面与浏览器里看到的、以及探针测到的，是同一套 f(t)。
//
// 用法：
//   node tools/promo-render.mjs <promo.html> <outDir> [fps] [from] [to] [png|jpeg]
// 例：
//   node tools/promo-render.mjs file:///D:/SliceArc-oss/docs/promo.html D:/SliceArc/artifacts/frames 30 0 22.4 png
import { setTimeout as sleep } from 'node:timers/promises';
import { writeFile, mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const PORT = Number(process.env.CDP_PORT || 9222);
const FILE = process.argv[2];
const OUT = process.argv[3];
const FPS = Number(process.argv[4] || 30);
const FROM = Number(process.argv[5] ?? 0);
const TO = Number(process.argv[6] ?? 22.4);
const FMT = (process.argv[7] || 'png').toLowerCase();

if (!FILE || !OUT) { console.error('用法: node promo-render.mjs <file:///…promo.html> <outDir> [fps] [from] [to] [png|jpeg]'); process.exit(2); }
await mkdir(OUT, { recursive: true });

const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find(x => x.type === 'page');
if (!page) { console.error('找不到 page target：先把带 --remote-debugging-port 的无头浏览器起起来'); process.exit(2); }
const ws = new WebSocket(page.webSocketDebuggerUrl);
let seq = 0; const pending = new Map(); const waits = [];
const send = (method, params = {}) => new Promise((res, rej) => {
  const id = ++seq; pending.set(id, { res, rej }); ws.send(JSON.stringify({ id, method, params }));
});
ws.addEventListener('message', ev => {
  const m = JSON.parse(ev.data);
  if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); }
  else if (m.method === 'Page.loadEventFired') { while (waits.length) waits.shift()(); }
});
await new Promise(r => ws.addEventListener('open', r));
await send('Page.enable'); await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });

const loaded = new Promise(r => waits.push(r));
await send('Page.navigate', { url: `${FILE}?t=0&clean=1` });
await loaded; await sleep(250);

const total = Math.max(1, Math.round((TO - FROM) * FPS));
const t0 = Date.now();
const pad = n => String(n).padStart(4, '0');
for (let i = 0; i < total; i++) {
  const t = FROM + i / FPS;
  await send('Runtime.evaluate', { expression: `__seek(${t.toFixed(4)})` });
  const shot = await send('Page.captureScreenshot', FMT === 'png' ? { format: 'png' } : { format: 'jpeg', quality: 92 });
  const ext = FMT === 'png' ? 'png' : 'jpg';
  await writeFile(join(OUT, `f${pad(i)}.${ext}`), Buffer.from(shot.data, 'base64'));
  if (i % 30 === 0 || i === total - 1) {
    const el = (Date.now() - t0) / 1000;
    const eta = i > 0 ? (el / (i + 1)) * (total - i - 1) : 0;
    process.stdout.write(`\r  ${i + 1}/${total} 帧  t=${t.toFixed(2)}s  已用 ${el.toFixed(0)}s  预计还需 ${eta.toFixed(0)}s   `);
  }
}
process.stdout.write('\n');
console.log(`完成：${total} 帧 → ${OUT}（${(total / FPS).toFixed(2)}s @ ${FPS}fps，总耗时 ${((Date.now() - t0) / 1000).toFixed(0)}s）`);
ws.close();
