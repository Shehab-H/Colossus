import { describe, expect, it } from 'vitest';
import { Field, Float32, Int32, List, Table, Utf8, makeBuilder, makeVector, tableFromIPC, tableToIPC, vectorFromArray } from 'apache-arrow';
import type { ViewConfig } from './manifest';
import { columnValue, decodeTile, tileBytes, type DictColumn, type Utf8Column } from './tileData';
import { MISSING_CODE, NULL_DAY, type FilterSlots } from './gpuFilter';

const utf8Vec = (vals: (string | null)[]) => {
  const b = makeBuilder({ type: new Utf8(), nullValues: [null] });
  for (const v of vals) b.append(v);
  return b.finish().toVector();
};

// IPC round-trip so decode sees exactly what fetchArrowTable produces (single-chunk stream data).
const roundTrip = (cols: Record<string, unknown>) => tableFromIPC(tableToIPC(new Table(cols as never)));

const pointView = (channels: ViewConfig['source']['channels'], inspect?: ViewConfig['inspect']): ViewConfig => ({
  id: 't',
  viewport: 'geo',
  mark: 'point',
  source: { adapter: 'test', query: '', geometry: { kind: 'lonLat' }, channels },
  encoding: { color: { channel: channels[0].name, type: 'categorical' } },
  inspect,
});

describe('decodeTile', () => {
  const xs = makeVector(new Float32Array([0, 1, 2]));
  const ys = makeVector(new Float32Array([10, 11, 12]));

  it('dict-codes a plain utf8 dimension and keeps values addressable', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const d = decodeTile(view, t);

    expect(d.count).toBe(3);
    expect([...d.positions!]).toEqual([0, 10, 1, 11, 2, 12]);
    const col = d.values.feature as DictColumn;
    expect(col.kind).toBe('dict');
    expect(col.codes).toBeInstanceOf(Uint8Array);
    expect(col.dict).toEqual(['a', 'b']);
    expect([0, 1, 2].map((i) => columnValue(col, i))).toEqual(['a', 'b', 'a']);
  });

  it('keeps identity channels as raw utf8, decoded per row (incl. unicode)', () => {
    const t = roundTrip({ x: xs, y: ys, name: utf8Vec(['Zürich', '東京', 'X']) });
    const view = pointView(
      [{ name: 'name', column: 'name', role: 'identity', type: 'dict' }],
      { title: 'name', channels: [] },
    );
    const col = decodeTile(view, t).values.name as Utf8Column;
    expect(col.kind).toBe('utf8');
    expect([0, 1, 2].map((i) => columnValue(col, i))).toEqual(['Zürich', '東京', 'X']);
  });

  it('copies numeric measures into a Float32Array', () => {
    const t = roundTrip({ x: xs, y: ys, population: makeVector(new Float32Array([5, 6, 7])) });
    const view = pointView([{ name: 'population', column: 'population', role: 'measure', type: 'f32' }]);
    const col = decodeTile(view, t).values.population;
    expect(col).toBeInstanceOf(Float32Array);
    expect([...(col as Float32Array)]).toEqual([5, 6, 7]);
  });

  it('reads arrow dictionary-encoded columns without a per-row scan', () => {
    const t = roundTrip({ x: xs, y: ys, feature: vectorFromArray(['p', 'q', 'p']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const col = decodeTile(view, t).values.feature as DictColumn;
    expect(col.kind).toBe('dict');
    expect([0, 1, 2].map((i) => columnValue(col, i))).toEqual(['p', 'q', 'p']);
  });

  it('renders nulls as the "null" category, matching String(col.get(i))', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', null, 'b']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const col = decodeTile(view, t).values.feature as DictColumn;
    expect(columnValue(col, 1)).toBe('null');
  });

  it('bails to raw utf8 when a dimension exceeds the dictionary cap', () => {
    const n = 70000;
    const vals = Array.from({ length: n }, (_, i) => `v${i}`);
    const t = roundTrip({
      x: makeVector(new Float32Array(n)),
      y: makeVector(new Float32Array(n)),
      big: utf8Vec(vals),
    });
    const view = pointView([{ name: 'big', column: 'big', role: 'dimension', type: 'dict' }]);
    const col = decodeTile(view, t).values.big as Utf8Column;
    expect(col.kind).toBe('utf8');
    expect(columnValue(col, 0)).toBe('v0');
    expect(columnValue(col, n - 1)).toBe(`v${n - 1}`);
  });

  it('normalizes temporal day-count columns to ISO dates', () => {
    // 19723 days after 1970-01-01 = 2024-01-01
    const t = roundTrip({ x: xs, y: ys, day: makeVector(new Int32Array([19723, 19723, 0])) });
    const view = pointView([{ name: 'day', column: 'day', role: 'temporal', type: 'date' }]);
    const col = decodeTile(view, t).values.day as DictColumn;
    expect(col.kind).toBe('dict');
    expect(columnValue(col, 0)).toBe('2024-01-01');
    expect(columnValue(col, 2)).toBe('1970-01-01');
  });

  it('estimates resident bytes from the actual buffers', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const d = decodeTile(view, t);
    // positions (3×2×4B) + dict codes (3×1B) + dictionary strings — small but non-zero.
    expect(tileBytes(d)).toBeGreaterThanOrEqual(24 + 3);
  });
});

describe('decodeTile filterValues (GPU filter slots)', () => {
  const xs = makeVector(new Float32Array([0, 1, 2]));
  const ys = makeVector(new Float32Array([10, 11, 12]));

  const dimSlots = (categories: string[]): FilterSlots => ({
    specs: [{ name: 'feature', kind: 'dimension', categories }],
    size: 1,
  });

  it('builds per-mark canonical codes for a dimension slot', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const d = decodeTile(view, t, dimSlots(['a', 'b']));
    expect(d.count).toBe(3);
    expect([...d.filterValues!]).toEqual([0, 1, 0]);
  });

  it('remaps arrow-dictionary codes to the canonical order (not the tile-local order)', () => {
    // canonical order [b, a]: 'a'→1, 'b'→0, regardless of the tile's own dictionary order.
    const t = roundTrip({ x: xs, y: ys, feature: vectorFromArray(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const d = decodeTile(view, t, dimSlots(['b', 'a']));
    expect([...d.filterValues!]).toEqual([1, 0, 1]);
  });

  it('maps a value absent from the canonical list to the missing sentinel', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'c', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const d = decodeTile(view, t, dimSlots(['a', 'b']));
    // f32-rounded compare: the sentinel through a Float32Array is fround(MISSING_CODE), not the exact constant.
    expect(d.filterValues).toEqual(Float32Array.from([0, MISSING_CODE, 0]));
  });

  it('builds day numbers for a temporal slot and NULL_DAY for null', () => {
    // 19723 days after 1970-01-01 = 2024-01-01
    const t = roundTrip({ x: xs, y: ys, day: makeVector(new Int32Array([19723, 0, 0])) });
    const view = pointView([{ name: 'day', column: 'day', role: 'temporal', type: 'date' }]);
    const slots: FilterSlots = { specs: [{ name: 'day', kind: 'temporal' }], size: 1 };
    const d = decodeTile(view, t, slots);
    expect([...d.filterValues!].slice(0, 2)).toEqual([19723, 0]);

    const tn = roundTrip({ x: xs.slice(0, 1), y: ys.slice(0, 1), day: utf8Vec([null]) });
    const dn = decodeTile(view, tn, slots);
    expect(dn.filterValues![0]).toBe(Math.fround(NULL_DAY));
  });

  it('interleaves multiple slots per mark in slot order', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']), day: makeVector(new Int32Array([0, 19723, 0])) });
    const view = pointView([
      { name: 'feature', column: 'feature', role: 'dimension', type: 'dict' },
      { name: 'day', column: 'day', role: 'temporal', type: 'date' },
    ]);
    const slots: FilterSlots = {
      specs: [{ name: 'feature', kind: 'dimension', categories: ['a', 'b'] }, { name: 'day', kind: 'temporal' }],
      size: 2,
    };
    const d = decodeTile(view, t, slots);
    // [code0, day0, code1, day1, code2, day2]
    expect([...d.filterValues!]).toEqual([0, 0, 1, 19723, 0, 0]);
  });

  it('fills the missing sentinel for a slot channel the tile lacks', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    const slots: FilterSlots = { specs: [{ name: 'absent', kind: 'dimension', categories: ['a'] }], size: 1 };
    const d = decodeTile(view, t, slots);
    expect(d.filterValues).toEqual(Float32Array.from([MISSING_CODE, MISSING_CODE, MISSING_CODE]));
  });

  it('no slots → no filterValues attribute', () => {
    const t = roundTrip({ x: xs, y: ys, feature: utf8Vec(['a', 'b', 'a']) });
    const view = pointView([{ name: 'feature', column: 'feature', role: 'dimension', type: 'dict' }]);
    expect(decodeTile(view, t, null).filterValues).toBeUndefined();
    expect(decodeTile(view, t).filterValues).toBeUndefined();
  });

  it('decodes polygon geometry, tile-global triangles, and per-vertex filter expansion', () => {
    const listVec = <T>(rows: number[][], child: T) => {
      const b = makeBuilder({ type: new List(new Field('item', child as never, false)) });
      for (const r of rows) b.append(r as never);
      return b.finish().toVector();
    };
    const t = roundTrip({
      geometry: listVec(
        [
          [0, 0, 1, 0, 1, 1, 0, 1], // square, 4 verts
          [5, 5, 6, 5, 5, 6], // triangle, 3 verts
        ],
        new Float32(),
      ),
      triangles: listVec([[0, 1, 2, 0, 2, 3], [0, 1, 2]], new Int32()),
      region: utf8Vec(['a', 'b']),
      val: makeVector(new Float32Array([1, 2])),
    });
    const view: ViewConfig = {
      id: 't',
      viewport: 'geo',
      mark: 'polygon',
      source: {
        adapter: 'test',
        query: '',
        geometry: { kind: 'quadkey' },
        channels: [
          { name: 'region', column: 'region', role: 'dimension', type: 'dict' },
          { name: 'val', column: 'val', role: 'measure', type: 'f32' },
        ],
      },
      encoding: { color: { channel: 'val' } },
    };

    const full = decodeTile(view, t);
    expect(full.count).toBe(2);
    expect([...full.polyStartIndices!]).toEqual([0, 4, 7]);
    expect([...full.polyTriangles!]).toEqual([0, 1, 2, 0, 2, 3, 4, 5, 6]);

    const slots: FilterSlots = { specs: [{ name: 'region', kind: 'dimension', categories: ['a', 'b'] }], size: 1 };
    const d = decodeTile(view, t, slots);
    // per-vertex: mark 0 ('a'→0) over its 4 verts, mark 1 ('b'→1) over its 3 verts
    expect([...d.filterValues!]).toEqual([0, 0, 0, 0, 1, 1, 1]);
    expect(d.filterValues!.length).toBe(d.vertexCount! * 1);
  });
});
