import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import type { SlabAxis } from './manifest';
import {
  buildFoldContext,
  type CompanionData,
  type CompanionDim,
  foldTile,
  type MeasureExpr,
  parseMeasure,
} from './measures';
import { dayNumber } from './dates';
import { foldSlab, type SlabData } from './slab';

// The SAME cross-language fixture the C# SlabFormatTests pins the writer against. Here the TS fold over the
// fixture's sparse CSR and dense cumulative encodings must reproduce fixture.folds — and agree with the
// row-form foldTile built from the same facts, so the fixture's hand-computed values are cross-checked.
interface Fixture {
  axes: Record<string, { kind: 'categorical' | 'ordered'; domain: string[] }>;
  grainOrder: string[];
  partials: string[];
  cumulative: string[];
  cellCount: number;
  markCount: number;
  facts: { mki: number; operator: string; quarter: string; tests: number; download_mbps: number }[];
  sparse: { offsets: number[]; cellIds: number[]; planes: Record<string, number[]> };
  dense: { planes: Record<string, (number | null)[]> };
  measures: { name: string; expr: string }[];
  folds: { context: Record<string, string>; expect: Record<string, (number | null)[]> }[];
}

const fx: Fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/slab-cases.json', import.meta.url), 'utf8'));

// Axes in cell order: categorical (operator) outer, ordered (quarter) fastest — SLAB-FORMAT §2.
const axes: SlabAxis[] = fx.grainOrder.map((name) => ({
  name,
  kind: fx.axes[name].kind,
  cardinality: fx.axes[name].domain.length,
  cumulative: fx.axes[name].kind === 'ordered',
  domain: fx.axes[name].domain,
}));

const strides = (() => {
  const out = new Array<number>(axes.length);
  let s = 1;
  for (let i = axes.length - 1; i >= 0; i--) {
    out[i] = s;
    s *= axes[i].cardinality;
  }
  return out;
})();

const f32 = (a: (number | null)[]): Float32Array => Float32Array.from(a, (v) => (v === null ? NaN : v));

const sparseData: SlabData = {
  layout: 'sparse',
  markCount: fx.markCount,
  cells: fx.cellCount,
  axes,
  strides,
  offsets: Int32Array.from(fx.sparse.offsets),
  cellIds: Uint8Array.from(fx.sparse.cellIds),
  planes: Object.fromEntries(fx.partials.map((p) => [p, f32(fx.sparse.planes[p])])),
  decodedBytes: 0,
};

const denseData: SlabData = {
  layout: 'dense',
  markCount: fx.markCount,
  cells: fx.cellCount,
  axes,
  strides,
  planes: Object.fromEntries(fx.partials.map((p) => [p, f32(fx.dense.planes[p])])),
  decodedBytes: 0,
};

// Row-form companion from the same facts (one row per fact = grain cell here) — the frozen oracle.
const rowData: CompanionData = {
  rowCount: fx.facts.length,
  mki: Int32Array.from(fx.facts, (f) => f.mki),
  dim: {
    operator: {
      dict: fx.axes.operator.domain,
      codes: Uint8Array.from(fx.facts, (f) => fx.axes.operator.domain.indexOf(f.operator)),
    } as CompanionDim,
  },
  temporalDays: { quarter: Float32Array.from(fx.facts, (f) => dayNumber(f.quarter)) },
  partial: {
    sum__tests: Float32Array.from(fx.facts, (f) => f.tests),
    cnt: Float32Array.from(fx.facts, () => 1),
    swp__download_mbps__tests: Float32Array.from(fx.facts, (f) => f.download_mbps * f.tests),
    max__tests: Float32Array.from(fx.facts, (f) => f.tests),
  },
};

const view = {
  source: {
    channels: [
      { name: 'operator', column: 'operator', role: 'dimension', type: 'dict' },
      { name: 'quarter', column: 'quarter', role: 'temporal', type: 'date' },
    ],
  },
} as unknown as Parameters<typeof buildFoldContext>[0];

const measures = fx.measures.map((m) => ({ name: m.name, ast: parseMeasure(m.expr) as MeasureExpr }));
const domains = { operator: fx.axes.operator.domain };

function assertFold(actual: Record<string, Float32Array | Uint16Array>, expect_: Record<string, (number | null)[]>, label: string) {
  for (const [name, exp] of Object.entries(expect_)) {
    const got = actual[name];
    for (let m = 0; m < exp.length; m++) {
      const e = exp[m];
      const tag = `${label} ${name}[${m}]`;
      if (e === null) expect(Number.isNaN(got[m]), tag).toBe(true);
      else if (got instanceof Uint16Array) expect([tag, got[m]]).toEqual([tag, e]);
      else expect(got[m], tag).toBeCloseTo(e, 4);
    }
  }
}

describe('slab fold (shared fixture)', () => {
  test('sparse, dense, and row-form folds all reproduce every fixture context', () => {
    expect(fx.folds.length).toBeGreaterThan(0);
    for (const c of fx.folds) {
      const ctx = buildFoldContext(view, c.context);
      assertFold(foldSlab(sparseData, measures, ctx, fx.markCount, domains), c.expect, `sparse ${JSON.stringify(c.context)}`);
      assertFold(foldSlab(denseData, measures, ctx, fx.markCount, domains), c.expect, `dense ${JSON.stringify(c.context)}`);
      // Cross-check the fixture's own expected values against the frozen row-form fold.
      const row = foldTile(rowData, measures, ctx, fx.markCount, domains);
      assertFold(row, c.expect, `row ${JSON.stringify(c.context)}`);
    }
  });
});
