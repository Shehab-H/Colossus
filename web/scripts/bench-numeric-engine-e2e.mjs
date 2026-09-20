import { chromium } from 'playwright';
import os from 'node:os';

const base = process.argv[2] ?? 'http://127.0.0.1:5099';
const backgroundUsers = Number(process.argv[3] ?? 64);
const iterations = Number(process.argv[4] ?? 100);
const headed = process.argv[5] === 'headed';
const downMbps = Number(process.argv[6] ?? 0);
const networkLatencyMs = Number(process.argv[7] ?? 0);

const cpuSnapshot = () => os.cpus().reduce((sum, cpu) => {
  const busy = cpu.times.user + cpu.times.nice + cpu.times.sys + cpu.times.irq;
  return {busy:sum.busy + busy, total:sum.total + busy + cpu.times.idle};
}, {busy:0, total:0});

const browser = await chromium.launch({headless:!headed, args:[
  '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding',
  '--disable-backgrounding-occluded-windows'
]});
const page = await browser.newPage({viewport:{width:1280,height:720}});
if (downMbps > 0 || networkLatencyMs > 0) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', {
    offline:false,
    latency:networkLatencyMs,
    downloadThroughput:downMbps > 0 ? downMbps * 1_000_000 / 8 : -1,
    uploadThroughput:downMbps > 0 ? downMbps * 1_000_000 / 8 : -1,
    connectionType:'cellular4g'
  });
}
await page.goto(base, {waitUntil:'load'});
const renderer = await page.evaluate(() => window.renderer);
const browserSession = await page.evaluate(() => window.createSession());
await fetch(`${base}/load/start?users=${backgroundUsers}`, {method:'POST'});
await fetch(`${base}/metrics/reset`, {method:'POST'});
const systemCpu0 = cpuSnapshot();
const results = await page.evaluate(
  async ({id, iterations}) => window.run(id, iterations, 8),
  {id:browserSession, iterations});
const load = await (await fetch(`${base}/load/stop`, {method:'POST'})).json();
const systemCpu1 = cpuSnapshot();
const server = await (await fetch(`${base}/metrics`)).json();
await browser.close();

const percentile = (name, p) => {
  const values = results.map(x => x[name]).sort((a,b)=>a-b);
  return values[Math.max(0, Math.ceil(values.length*p)-1)];
};
const components = ['total','headers','body','decode','gpu','paint','queue','compute','encode','uncompressed','transfer'];
const systemBusy = systemCpu1.busy - systemCpu0.busy;
const systemTotal = systemCpu1.total - systemCpu0.total;
const summary = {
  backgroundUsers, backgroundRequests:load.requests, iterations, renderer,
  network:{downMbps, latencyMs:networkLatencyMs}, server,
  systemCpuPercent:100 * systemBusy / systemTotal,
  systemEffectiveCores:os.cpus().length * systemBusy / systemTotal
};
for (const name of components) {
  summary[name] = {p50:percentile(name,.5), p95:percentile(name,.95), p99:percentile(name,.99)};
}
summary.changed = results.reduce((sum,x)=>sum+x.changed,0)/results.length;
console.log(JSON.stringify(summary,null,2));
