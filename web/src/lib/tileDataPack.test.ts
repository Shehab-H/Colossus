import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { Binary, Table, Utf8, makeBuilder, makeVector, tableFromIPC, tableToIPC } from 'apache-arrow';
import type { ViewConfig } from './manifest';
import { GEOM_GROUP } from './manifest';
import { columnValue, decodeTile, decodeTileBlocks, tileBytes, type DictColumn } from './tileData';
import type { FilterSlots } from './gpuFilter';

// Each block is a standalone single-batch Arrow IPC stream, exactly as RenderPackWriter emits it — parsed
// from its own ArrayBuffer, so each column is a view over its own block buffer (not one tile buffer).
const block = (cols: Record<string, unknown>): { table: Table; buffer: ArrayBuffer } => {
  const buffer = tableToIPC(new Table(cols as never)).slice().buffer;
  return { table: tableFromIPC(new Uint8Array(buffer)), buffer };
};

/** Split a whole tile's columns into pack groups: `@geom` holds the geometry-equivalent columns, every
 *  other column is its own group — the layout RenderPackWriter.SplitTile produces. */
function pack(geomCols: Record<string, unknown>, rest: Record<string, unknown>, want?: string[]) {
  const tables: Record<string, Table> = {};
  const buffers: Record<string, ArrayBuffer> = {};
  const add = (g: string, cols: Record<string, unknown>) => {
    const b = block(cols);
    tables[g] = b.table;
    buffers[g] = b.buffer;
  };
  add(GEOM_GROUP, geomCols);
  for (const [name, col] of Object.entries(rest)) {
    if (want && !want.includes(name)) continue;
    add(name, { [name]: col });
  }
  return { tables, buffers };
}

const utf8Vec = (vals: string[]) => {
  const b = makeBuilder({ type: new Utf8() });
  for (const v of vals) b.append(v);
  return b.finish().toVector();
};

const binaryVec = (rows: Uint8Array[]) => {
  const b = makeBuilder({ type: new Binary() });
  for (const r of rows) b.append(r);
  return b.finish().toVector();
};

const pointView = (channels: ViewConfig['source']['channels'], inspect?: ViewConfig['inspect']): ViewConfig => ({
  id: 't',
  viewport: 'geo',
  mark: 'point',
  source: { adapter: 'test', query: '', geometry: { kind: 'lonLat' }, channels },
  encoding: { color: { channel: channels[0].name, type: 'categorical' } },
  inspect,
});

describe('render pack decode', () => {
  const xs = makeVector(new Float32Array([0, 1, 2]));
  const ys = makeVector(new Float32Array([10, 11, 12]));
  const speed = makeVector(new Float32Array([5.5, 6.5, 7.5]));
  const channels: ViewConfig['source']['channels'] = [
    { name: 'feature', column: 'feature', role: 'dimension', type: 'dict' },
    { name: 'speed', column: 'speed', role: 'measure', type: 'f32' },
    { name: 'name', column: 'name', role: 'identity', type: 'string' },
  ];
  const view = pointView(channels, { channels: ['name'] });
  const cols = { x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']), speed, name: utf8Vec(['p', 'q', 'r']) };

  it('a fully-fetched packed tile decodes identically to the whole-tile path', () => {
    const whole = tableToIPC(new Table(cols as never)).slice().buffer;
    const expected = decodeTile(view, tableFromIPC(new Uint8Array(whole)), null, 2, whole);

    const { tables, buffers } = pack({ x: xs, y: ys }, { feature: cols.feature, speed, name: cols.name });
    const got = decodeTileBlocks(view, tables, buffers, null);

    expect(got.count).toBe(expected.count);
    expect([...got.positions!]).toEqual([...expected.positions!]);
    expect(Object.keys(got.values).sort()).toEqual(Object.keys(expected.values).sort());
    expect([...(got.values.speed as Float32Array)]).toEqual([...(expected.values.speed as Float32Array)]);
    const gf = got.values.feature as DictColumn;
    const ef = expected.values.feature as DictColumn;
    expect(gf.dict).toEqual(ef.dict);
    expect([0, 1, 2].map((i) => columnValue(gf, i))).toEqual([0, 1, 2].map((i) => columnValue(ef, i)));
    expect([0, 1, 2].map((i) => columnValue(got.values.name, i))).toEqual(['p', 'q', 'r']);
  });

  it('a first-paint fetch carries only the requested groups; the rest are simply absent', () => {
    // firstPaint = [@geom, feature] — the colour channel. speed/name are lazy.
    const { tables, buffers } = pack({ x: xs, y: ys }, { feature: cols.feature, speed, name: cols.name }, ['feature']);
    const d = decodeTileBlocks(view, tables, buffers, null);

    expect(d.count).toBe(3);
    expect([...d.positions!]).toEqual([0, 10, 1, 11, 2, 12]);
    expect(Object.keys(d.values)).toEqual(['feature']);
    expect(d.values.speed).toBeUndefined(); // not fetched — not a decode failure
    expect(d.values.name).toBeUndefined();
  });

  it('builds GPU filter slots from the blocks, not the whole tile', () => {
    const slots: FilterSlots = {
      size: 1,
      specs: [{ name: 'feature', kind: 'dimension', categories: ['a', 'b'] }],
    } as FilterSlots;
    const { tables, buffers } = pack({ x: xs, y: ys }, { feature: cols.feature }, ['feature']);
    const d = decodeTileBlocks(view, tables, buffers, slots);

    expect(d.filterValues).toBeInstanceOf(Float32Array);
    expect([...d.filterValues!]).toEqual([0, 1, 0]); // canonical codes for a,b,a
  });

  it('counts every block buffer once and never double-counts a merged column', () => {
    const { tables, buffers } = pack({ x: xs, y: ys }, { feature: cols.feature, speed, name: cols.name });
    const d = decodeTileBlocks(view, tables, buffers, null);

    const blockTotal = Object.values(buffers).reduce((a, b) => a + b.byteLength, 0);
    const bytes = tileBytes(d);
    expect(bytes).toBeGreaterThanOrEqual(blockTotal);
    // Columns are views into blocks already counted; only the derived positions array is extra.
    expect(bytes).toBeLessThan(blockTotal + d.positions!.byteLength + 1024);
  });

  it('decodes an area-mark tile through the geom3 block', () => {
    // A real encoder payload from the cross-language fixture — the same bytes the C# writer emits.
    const fixture = JSON.parse(
      readFileSync(new URL('../../../tests/fixtures/geometry-codec-cases.json', import.meta.url), 'utf8'),
    ) as { cases: { name: string; payloadBase64: string; positions: number[]; triangles: number[] }[] };
    const c = fixture.cases.find((k) => k.name === 'delta-multipart')!;
    const payload = Uint8Array.from(Buffer.from(c.payloadBase64, 'base64'));

    const polyView: ViewConfig = {
      id: 't',
      viewport: 'geo',
      mark: 'polygon',
      source: {
        adapter: 'test',
        query: '',
        geometry: { kind: 'wkt' },
        channels: [{ name: 'area', column: 'area', role: 'measure', type: 'f32' }],
      },
      encoding: { color: { channel: 'area', type: 'linear' } },
    };
    const { tables, buffers } = pack(
      { geom3: binaryVec([payload]) },
      { area: makeVector(new Float32Array([3.5])) },
    );
    const d = decodeTileBlocks(polyView, tables, buffers, null);

    expect(d.count).toBe(1);
    expect([...d.polyPositions!]).toEqual([...Float32Array.from(c.positions)]);
    expect([...d.polyTriangles!]).toEqual(c.triangles);
    expect([...(d.values.area as Float32Array)]).toEqual([3.5]);
  });
});
