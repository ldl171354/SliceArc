// promo-dump.mjs — 针对单个时刻，把怀疑元素的"真实计算样式 + 盒子"打出来
// 用法: node tools/promo-dump.mjs file:///.../promo.html 5.5 head,ptr,stage,world
import { setTimeout as sleep } from 'node:timers/promises';
const PORT = Number(process.env.CDP_PORT || 9222);
const FILE = process.argv[2];
const t = Number(process.argv[3] || 5.5);
const IDS = (process.argv[4] || 'head').split(',');

const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
const page = list.find(x => x.type === 'page');
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
  // 锁死视口：舞台按 1600×900 设计，视口不对会整体缩放，测量全失真
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });
const loaded = new Promise(r => waits.push(r));
await send('Page.navigate', { url: `${FILE}?t=${t}&clean=1` });
await loaded; await sleep(150);

const expr = `(() => {
  const ids = ${JSON.stringify(IDS)};
  const out = { viewport: { w: innerWidth, h: innerHeight, dpr: devicePixelRatio }, t: window.__t };
  for (const id of ids) {
    const el = document.getElementById(id);
    if (!el) { out[id] = 'MISSING'; continue; }
    const r = el.getBoundingClientRect(); const cs = getComputedStyle(el);
    out[id] = {
      rect: { x: +r.x.toFixed(1), y: +r.y.toFixed(1), w: +r.width.toFixed(1), h: +r.height.toFixed(1) },
      width: cs.width, height: cs.height, position: cs.position, display: cs.display,
      transform: cs.transform, left: cs.left, top: cs.top, opacity: cs.opacity,
      inlineStyle: el.getAttribute('style') || ''
    };
  }
  return JSON.stringify(out, null, 2);
})()`;
const { result } = await send('Runtime.evaluate', { expression: expr, returnByValue: true });
console.log(result.value);
ws.close();
