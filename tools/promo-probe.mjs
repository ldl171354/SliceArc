// promo-probe.mjs — 宣传动画的逐帧"眼睛"
//
// 本机没有配视觉 provider，看不了图；所以改成读**真实几何与可见性**：
// 用 Node 内置 WebSocket 直接说 Chrome DevTools Protocol（不装任何包），
// 在无头浏览器里把页面定格到若干时刻，取回每个元素的 getBoundingClientRect()
// 与 opacity，然后判定：
//   offFrame   —— 有东西跑出 1600×900 的舞台（成片会被裁掉）
//   invisible  —— 该出现的元素 opacity < 0.05
//   collide    —— 两个"主角"级别元素重叠
//   still      —— 定格段两帧的样式完全一致（节奏靠静止才立得住）
//
// 用法：
//   1) 先起一个带调试端口的无头浏览器：
//      msedge --headless=new --disable-gpu --remote-debugging-port=9222 ^
//             --user-data-dir=D:\SliceArc\artifacts\edge-cdp about:blank
//   2) node tools/promo-probe.mjs file:///D:/SliceArc-oss/docs/promo.html "0.9,5.5,14.2,16.4"
import { setTimeout as sleep } from 'node:timers/promises';

const PORT = Number(process.env.CDP_PORT || 9222);
const FILE = process.argv[2] || 'file:///D:/SliceArc-oss/docs/promo.html';
const TIMES = (process.argv[3] || '0.9,2.2,3.2,5.5,7.6,11.5,14.2,16.4,18.6,20.8')
  .split(',').map(s => Number(s.trim())).filter(n => Number.isFinite(n));

const IDS = ['title', 'hud', 'disk', 'pkgTag', 'vols', 'out', 'head', 'ptr', 'explorer', 'menu', 'ghost', 'end'];
// 每个时刻"应该看得见"的元素：用来抓"该有东西却是空的"这类错
const EXPECT = [
  { from: 1.8, to: 15.0, ids: ['disk'] },
  { from: 3.6, to: 17.0, ids: ['out'] },
  { from: 3.2, to: 15.0, ids: ['vols', 'head'] },
  { from: 15.5, to: 17.2, ids: ['explorer'] },
  { from: 18.0, to: 19.3, ids: ['menu'] },
  { from: 20.0, to: 22.4, ids: ['end'] },
];
// 同一时刻不该同时当主角的元素对
const COLLIDE = [['menu', 'end']];

const PROBE = `(() => {
  const ids = ${JSON.stringify(IDS)};
  const num = v => Math.round(v * 100) / 100;
  const box = el => { const r = el.getBoundingClientRect();
    return { x: num(r.x), y: num(r.y), w: num(r.width), h: num(r.height) }; };
  const o = { t: window.__t, meta: window.__meta, els: {}, vols: null, text: {} };
  for (const id of ids) {
    const el = document.getElementById(id);
    if (!el) { o.els[id] = null; continue; }
    const cs = getComputedStyle(el);
    o.els[id] = Object.assign(box(el), {
      op: num(parseFloat(cs.opacity)),
      disp: cs.display,
      vis: cs.visibility
    });
  }
  const vols = [...document.querySelectorAll('.vol')];
  o.vols = {
    total: vols.length,
    visible: vols.filter(v => parseFloat(getComputedStyle(v).opacity) > 0.05).length,
    firstX: vols.length ? num(vols[0].getBoundingClientRect().x) : null,
    lastX: vols.length ? num(vols[vols.length - 1].getBoundingClientRect().right) : null
  };
  o.text = {
    hudNum: document.getElementById('hudNum')?.textContent,
    hudLbl: document.getElementById('hudLbl')?.textContent,
    diskFree: document.getElementById('diskFree')?.textContent
  };
  o.styleOf = {};
  for (const id of ['out','head','ptr','menu','vols','end','ghost']) {
    const el = document.getElementById(id);
    if (el) o.styleOf[id] = el.getAttribute('style') || '';
  }
  return JSON.stringify(o);
})()`;

async function cdp() {
  const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  const page = list.find(t => t.type === 'page');
  if (!page) throw new Error('找不到 page target（先把无头浏览器起起来）');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  let seq = 0; const pending = new Map(); const waits = [];
  const send = (method, params = {}) => new Promise((res, rej) => {
    const id = ++seq; pending.set(id, { res, rej });
    ws.send(JSON.stringify({ id, method, params }));
  });
  ws.addEventListener('message', ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id); pending.delete(m.id);
      m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result);
    } else if (m.method === 'Page.loadEventFired') {
      while (waits.length) waits.shift()();
    }
  });
  await new Promise(r => ws.addEventListener('open', r));
  await send('Page.enable'); await send('Runtime.enable');
  // 锁死视口：舞台按 1600×900 设计，视口不对会整体缩放，测量全失真
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });
  const waitLoad = () => new Promise(r => waits.push(r));
  return { send, waitLoad, close: () => ws.close() };
}

const overlap = (a, b) => {
  if (!a || !b) return 0;
  const x = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x));
  const y = Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
  return x * y;
};

const { send, waitLoad, close } = await cdp();
const problems = [];
const rows = [];
for (const t of TIMES) {
  const loaded = waitLoad();
  await send('Page.navigate', { url: `${FILE}?t=${t}&clean=1` });
  await loaded; await sleep(120);
  const { result } = await send('Runtime.evaluate', { expression: PROBE, returnByValue: true });
  const o = JSON.parse(result.value);

  const bad = [];
  // 1) 出画 / 零面积
  for (const [id, b] of Object.entries(o.els)) {
    if (!b || b.op < 0.05) continue;
    if (b.x < -2 || b.y < -2 || b.x + b.w > 1602 || b.y + b.h > 902) bad.push(`${id} 出画 ${b.x},${b.y},${b.w}×${b.h}`);
    if (b.w < 1 || b.h < 1) bad.push(`${id} 零面积`);
  }
  // 2) 该出现却没出现
  for (const e of EXPECT) {
    if (t < e.from || t > e.to) continue;
    for (const id of e.ids) if ((o.els[id]?.op ?? 0) < 0.05) bad.push(`${id} 应为可见却 op=${o.els[id]?.op}`);
  }
  // 3) 主角重叠
  for (const [a, b] of COLLIDE) {
    const va = o.els[a], vb = o.els[b];
    if (!va || !vb || va.op < 0.05 || vb.op < 0.05) continue;   // 两个都看不见就谈不上重叠
    const ov = overlap(va, vb);
    if (ov > 40000) bad.push(`${a}×${b} 重叠 ${Math.round(ov)}px²（op ${va.op}/${vb.op}）`);
  }
  // 4) 卷块可见数 = 已入场(t > 2.60+0.075i) 且 未淡出完(t < HIT[i]+0.42)
  const stagger = 2.60, step = 0.075, fade = 0.42;
  const hits = o.meta?.hits || [];
  const expect = hits.filter((h, i) => t > stagger + step * i && t < h + fade).length;
  if (Math.abs(o.vols.visible - expect) > 1) bad.push(`卷块可见 ${o.vols.visible} ≠ 预期 ${expect}`);
  rows.push({ t, hud: `${o.text.hudNum} GB · ${o.text.hudLbl}`, vols: o.vols.visible, head: o.els.head ? Math.round(o.els.head.x) : null, bad });
  if (bad.length) problems.push({ t, bad });
  globalThis[`__style_${t}`] = o.styleOf;
}

console.log('时间     HUD                     可见卷  读头x   问题');
for (const r of rows) {
  console.log(`${String(r.t).padStart(5)}s  ${String(r.hud).padEnd(22)} ${String(r.vols).padStart(4)}  ${String(r.head).padStart(6)}   ${r.bad.length ? '⚠ ' + r.bad.join(' / ') : 'ok'}`);
}

// 定格段：两帧必须完全一致（静止是节奏的对手戏）
const stillA = 14.4, stillB = 14.9;
if (TIMES.includes(stillA) && TIMES.includes(stillB)) {
  const a = globalThis[`__style_${stillA}`], b = globalThis[`__style_${stillB}`];
  const diff = Object.keys(a).filter(k => a[k] !== b[k]);
  console.log(`\n定格 ${stillA}s vs ${stillB}s：${diff.length ? '⚠ 仍在动 → ' + diff.join(',') : '完全静止 ✅'}`);
  if (diff.length) problems.push({ t: `${stillA}/${stillB}`, bad: ['定格段没有静止：' + diff.join(',')] });
} else {
  console.log(`\n（提示：把 ${stillA},${stillB} 也放进时间列表，才能校验"定格段是否真静止"）`);
}

console.log(`\n${problems.length ? '❌ 发现 ' + problems.length + ' 个时刻有问题' : '✅ 全部时刻通过'}`);
close();
