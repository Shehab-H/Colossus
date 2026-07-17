// Diagnostic: does the local slab fold honour a temporal range context on the real bake?
// npx vite-node scripts/check-range-fold.ts -- mobile-dominance
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import type { Manifest } from '../src/lib/manifest';
import { buildFoldContext, parseMeasure } from '../src/lib/measures';
import { decodeSlab, foldSlab, slabPlanesForMeasures } from '../src/lib/slab';

const TILES = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tiles');
const view = process.argv.slice(2).find((a) => !a.startsWith('-'))!;
const latest = JSON.parse(readFileSync(join(TILES, view, 'latest.json'), 'utf8')) as { version: string };
const dir = join(TILES, view, latest.version);
const m = JSON.parse(readFileSync(join(dir, 'manifest.json'), 'utf8')) as Manifest;
const pack = readFileSync(join(dir, m.companionPack!.file));

const key = '5/17/19';
const markCount = m.tiles.find((t) => `${t.z}/${t.x}/${t.y}` === key)!.count;
const measures = (m.view.measures ?? []).map((mm) => ({ name: mm.name, ast: parseMeasure(mm.expr) }));
const colorMeasure = m.view.encoding!.color!.channel;
const active = measures.filter((x) => x.name === colorMeasure);
const domains: Record<string, string[]> = {};
for (const mm of active) {
  const ast = mm.ast;
  if ((ast.kind === 'argmax' || ast.kind === 'argmin') && m.channelDomains?.[ast.dimension]?.values)
    domains[ast.dimension] = m.channelDomains[ast.dimension]!.values!;
}

const planeDir = m.companionPack!.planeEntries![key];
const blocks: Record<string, ArrayBuffer> = {};
for (const p of slabPlanesForMeasures(m, [colorMeasure])) {
  const [off, len] = planeDir[p];
  const g = gunzipSync(pack.subarray(off, off + len));
  blocks[p] = g.buffer.slice(g.byteOffset, g.byteOffset + g.byteLength);
}
const slab = decodeSlab(blocks, m.companionSlab!);

console.log('quarter axis domain:', m.companionSlab!.axes.find((a) => a.name === 'quarter')?.domain);
console.log('channelDomains.quarter:', JSON.stringify(m.channelDomains?.quarter));

const report = (label: string, ctx: Record<string, string>) => {
  const cols = foldSlab(slab, active, buildFoldContext(m.view, ctx), markCount, domains);
  const col = cols[colorMeasure] as Uint16Array;
  const hist = new Map<number, number>();
  for (const v of col) hist.set(v, (hist.get(v) ?? 0) + 1);
  const unknown = hist.get(0xffff) ?? 0;
  console.log(`${label.padEnd(42)} unknown=${String(unknown).padStart(7)}/${col.length}  codes=${JSON.stringify([...hist].sort((a, b) => a[0] - b[0]).slice(0, 5))}`);
};

report('no context (all facts)', {});
report('operator=apex', { operator: 'apex' });
report('quarter 2025-01-01..2025-10-01 (ISO)', { quarter: '2025-01-01..2025-10-01' });
report('quarter 2024-01-01..2025-10-01 (full ISO)', { quarter: '2024-01-01..2025-10-01' });
report('quarter 19723..20362 (raw day numbers)', { quarter: '19723..20362' });
report('operator=apex + ISO range', { operator: 'apex', quarter: '2025-01-01..2025-10-01' });
