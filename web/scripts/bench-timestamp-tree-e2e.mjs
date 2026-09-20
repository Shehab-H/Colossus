import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://127.0.0.1:5107';
const iterations = Number(process.argv[3] ?? 3);
const downMbps = Number(process.argv[4] ?? 0);
const networkLatencyMs = Number(process.argv[5] ?? 0);

const browser = await chromium.launch({headless:true,args:[
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
await page.goto(base,{waitUntil:'load'});
const renderer = await page.evaluate(() => window.renderer);
const rows = await page.evaluate(iterations => window.run(iterations), iterations);
await browser.close();

const percentile = (name,p) => {
  const values=rows.map(row=>row[name]).sort((a,b)=>a-b);
  return values[Math.max(0,Math.ceil(values.length*p)-1)];
};
const summary={iterations,renderer,network:{downMbps,latencyMs:networkLatencyMs},bytes:rows[0]?.bytes};
for(const name of ['total','headers','body','decode','gpu','paint','queue','backend','merge','encode'])
  summary[name]={p50:percentile(name,.5),p95:percentile(name,.95),max:percentile(name,1)};
console.log(JSON.stringify(summary,null,2));
