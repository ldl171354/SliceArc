// promo-probe.mjs — 宣传片的逐帧"眼睛"（通用版：不写死元素清单，任意画幅）
//
// 本机没有视觉 provider，看不了图；所以读**真实几何与可见性**并做四条硬检查：
//   1) 出画       —— 可见元素跑到画幅外（成片会被裁掉）
//   2) 文字零面积 —— 该显示文字的元素却是 0×0（真正的"该有东西却没有"）
//                    （0 宽的进度条、纯定位容器不算，这是设计上的中间态）
//   3) 空帧       —— 某一时刻一个可见元素都没有
//   4) 纯函数契约 —— 同一 t 在"倒序重访"后样式必须逐字节一致
//                    （能抓到"读了上一帧自己的值"这类隐蔽 bug）
//
// 用法：
//   1) 起一个带调试端口的无头浏览器（见 promo-render.mjs 顶部说明）
//   2) node tools/promo-probe.mjs <file:///…html> "1,5,9,13.5,20" [1080x1920]
import { setTimeout as sleep } from 'node:timers/promises';

const PORT = Number(process.env.CDP_PORT || 9222);
const FILE = process.argv[2];
const TIMES = (process.argv[3] || '').split(',').map(s => Number(s.trim())).filter(Number.isFinite);
const SIZE = (process.argv[4] || '1600x900').toLowerCase().split('x').map(Number);
if (!FILE || !TIMES.length) { console.error('用法: node promo-probe.mjs <file> "0.9,5.5,…" [WxH]'); process.exit(2); }

const PROBE = `(() => {
  const n = v => Math.round(v * 100) / 100;
  const ids = [...document.querySelectorAll('[id]')].map(e => e.id).filter(Boolean);
  const els = {}, vis = [];
  for (const id of ids) {
    const el = document.getElementById(id);
    if (!el) continue;
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden') continue;
    if (el.offsetParent === null && cs.position !== 'fixed') continue;   // 祖先被 display:none 裹着
    let op = parseFloat(cs.opacity);
    for (let a = el.parentElement; a; a = a.parentElement) op *= parseFloat(getComputedStyle(a).opacity);  // 有效透明度
    const r = el.getBoundingClientRect();
    const leafText = el.children.length === 0 && el.textContent.trim().length > 0;
    els[id] = { x: n(r.x), y: n(r.y), w: n(r.width), h: n(r.height), op: n(op), leafText,
                style: (el.getAttribute('style') || '') };
    if (op > 0.05 && r.width > 0 && r.height > 0) vis.push(id);
  }
  return JSON.stringify({ t: window.__t, vp: [innerWidth, innerHeight], els, vis });
})()`;

// 站在屏上的候选（用于"这一秒该演到哪一站"的核对）
const STATIONS = ['hookPkg','stamp','stack','outCard','savedBig','montA','montB','montC','montD','montE','menuStage','safeStage','end'];

async function connect() {
  const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
  const page = list.find(x => x.type === 'page');
  if (!page) throw new Error('找不到 page target');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  let seq = 0; const pending = new Map(); const waits = [];
  const send = (m, p = {}) => new Promise((res, rej) => {
    const id = ++seq; pending.set(id, { res, rej }); ws.send(JSON.stringify({ id, method: m, params: p }));
  });
  ws.addEventListener('message', ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { const p = pending.get(m.id); pending.delete(m.id); m.error ? p.rej(new Error(JSON.stringify(m.error))) : p.res(m.result); }
    else if (m.method === 'Page.loadEventFired') { while (waits.length) waits.shift()(); }
  });
  await new Promise(r => ws.addEventListener('open', r));
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: SIZE[0], height: SIZE[1], deviceScaleFactor: 1, mobile: false });
  const loaded = new Promise(r => waits.push(r));
  await send('Page.navigate', { url: `${FILE}?t=0&clean=1` });
  await loaded; await sleep(200);
  return { send, ws };
}

const { send, ws } = await connect();
const seek = async t => {
  await send('Runtime.evaluate', { expression: `__seek(${t})` });
  const { result } = await send('Runtime.evaluate', { expression: PROBE, returnByValue: true });
  return JSON.parse(result.value);
};

const problems = [];
console.log(`画幅 ${SIZE[0]}×${SIZE[1]}   ${FILE}`);
console.log('\n  时间   可见  状态              屏上的站');
const snaps = new Map();
for (const t of TIMES) {
  const o = await seek(t);
  const bad = [];
  for (const [id, e] of Object.entries(o.els)) {
    if (e.op <= 0.05) continue;
    if (e.x < -2 || e.y < -2 || e.x + e.w > SIZE[0] + 2 || e.y + e.h > SIZE[1] + 2)
      bad.push(`${id} 出画(${e.x},${e.y},${e.w}×${e.h})`);
    if (e.leafText && (e.w < 1 || e.h < 1)) bad.push(`${id} 文字零面积`);
  }
  if (!o.vis.length) bad.push('空帧');
  // 文字压盖：所有可见的"纯文字元素"两两求交（父子关系已在 PROBE 里排除：有子元素的父节点不算 leafText）
  const texts = Object.entries(o.els).filter(([, e]) => e.leafText && e.op > 0.05 && e.w > 4 && e.h > 4);
  for (let i = 0; i < texts.length; i++) {
    for (let j = i + 1; j < texts.length; j++) {
      const [ida, a] = texts[i], [idb, b] = texts[j];
      const ix = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x));
      const iy = Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
      const ov = ix * iy, smaller = Math.min(a.w * a.h, b.w * b.h);
      if (ov > 240 && smaller > 0 && ov / smaller > 0.12)
        bad.push(`文字压盖 ${ida}×${idb} ${Math.round(ov)}px² (${(ov / smaller * 100).toFixed(0)}%)`);
    }
  }
  // 块级重叠：布局块互相压住是"文字叠在一起"的常见成因。
  // 白名单：bar×spaceWrap（读条本来就要扫过卷柱）、cur×任何（指针本来就压在东西上）
  const BLOCKS = ['topbar','headSpace','volHead','spaceWrap','savedBig','montA','montB','montC','montD','montE','headMenu','menuStage','menuFile','ctx','headSafe','safeStage','end'];
  const intended = (a, b) => (a === 'bar' || b === 'bar' || a === 'cur' || b === 'cur' || a === 'end' || b === 'end');  // #end 是整屏尾卡，本就盖住一切
  const vis = BLOCKS.filter(id => o.els[id] && o.els[id].op > 0.05 && o.els[id].w > 4 && o.els[id].h > 4);
  for (let i = 0; i < vis.length; i++) {
    for (let j = i + 1; j < vis.length; j++) {
      const a = o.els[vis[i]], b = o.els[vis[j]];
      if (intended(vis[i], vis[j])) continue;
      const ix = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x));
      const iy = Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
      const ov = ix * iy;
      if (ov > 900) bad.push(`块级重叠 ${vis[i]}×${vis[j]} ${Math.round(ov)}px²`);
    }
  }
  const st = STATIONS.filter(s => o.els[s] && o.els[s].op > 0.05);
  const busy = STATIONS.filter(s => o.els[s] && o.els[s].op > 0.5);
  if (busy.length > 2) bad.push(`同时有 ${busy.length} 个站在屏上（${busy.join(',')}）`);
  snaps.set(t, o.els);
  const state = bad.length ? '⚠ ' + bad.slice(0, 2).join(' / ') : 'ok';
  console.log(String(t).padStart(6) + 's ' + String(o.vis.length).padStart(4) + '  ' +
              state.padEnd(16) + ' ' + (st.join(',') || '(无)'));
  if (bad.length) problems.push({ t, bad });
}

console.log('\n契约检查：__seek(t) 是否纯函数（倒序重访同一批时刻）');
let mismatches = 0;
for (const t of [...TIMES].reverse()) {
  const o = await seek(t);
  const a = snaps.get(t) || {};
  const diff = [];
  for (const id of new Set([...Object.keys(a), ...Object.keys(o.els)])) {
    const x = a[id]?.style ?? '(无)', y = o.els[id]?.style ?? '(无)';
    if (x !== y) diff.push(id);
  }
  if (diff.length) { mismatches++; console.log(`  ⚠ t=${t}s 不一致：${diff.join(', ')}`); }
}
if (mismatches) problems.push({ t: '纯函数', bad: [`${mismatches} 个时刻重复访问结果不同`] });
else console.log('  ✅ 倒序重访完全一致 —— __seek 是纯函数');

console.log(`\n${problems.length ? '❌ ' + problems.length + ' 个时刻有问题' : '✅ 全部检查通过'}`);
ws.close();
