import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import {
  ARGMAX_UNKNOWN,
  type CompanionData,
  type FoldContext,
  foldTile,
  type MeasureExpr,
  parseMeasure,
} from './measures';

// The shared measure-grammar fixture — the SAME file the C# MeasureParser tests verify. If the TS
// parser drifts from the bake's, this fails. Read at runtime (Node) so the browser build never sees it.
interface Fixture {
  parse: { expr: string; ast: unknown }[];
  errors: { expr: string; message: string }[];
}
const fixture: Fixture = JSON.parse(
  readFileSync(new URL('../../../tests/fixtures/measure-cases.json', import.meta.url), 'utf8'),
);

describe('measure grammar (shared fixture)', () => {
  test('parseMeasure reproduces every fixture AST', () => {
    expect(fixture.parse.length).toBeGreaterThan(0);
    for (const c of fixture.parse) {
      expect([c.expr, parseMeasure(c.expr)]).toEqual([c.expr, c.ast]);
    }
  });

  test('parseMeasure rejects every fixture error case', () => {
    expect(fixture.errors.length).toBeGreaterThan(0);
    for (const c of fixture.errors) {
      expect(() => parseMeasure(c.expr), c.expr).toThrow(c.message);
    }
  });
});

const day = (iso: string): number => Math.floor(Date.parse(`${iso}T00:00:00Z`) / 86400000);

describe('foldTile', () => {
  // Two marks (tile rows 0 and 1); row 0 has apex(q1,q2) + zenith(q1) facts, row 1 one zenith(q1) fact.
  // Typed contract: mki joins each fact row to its mark's tile row; operator is dict codes in the
  // canonical order; quarter is day numbers.
  const companion: CompanionData = {
    rowCount: 4,
    mki: new Int32Array([0, 0, 0, 1]),
    dim: { operator: { codes: new Uint8Array([0, 0, 1, 1]), dict: ['apex', 'zenith'] } },
    temporalDays: { quarter: new Float32Array([day('2025-01-01'), day('2025-04-01'), day('2025-01-01'), day('2025-01-01')]) },
    partial: {
      sum__tests: new Float32Array([10, 5, 3, 8]),
      swp__download_mbps__tests: new Float32Array([500, 200, 270, 160]),
    },
  };
  const domains = { operator: ['apex', 'zenith'] };
  const measures: { name: string; ast: MeasureExpr }[] = [
    { name: 'total_tests', ast: parseMeasure('sum(tests)') },
    { name: 'avg_download', ast: parseMeasure('wavg(download_mbps, tests)') },
    { name: 'apex_share', ast: parseMeasure("share(sum(tests)) where operator = 'apex'") },
    { name: 'dominant_operator', ast: parseMeasure('argmax(operator, sum(tests))') },
  ];
  const fold = (ctx: FoldContext) => foldTile(companion, measures, ctx, 2, domains);
  const empty: FoldContext = { equals: {}, ranges: {} };

  test('default context reproduces every measure', () => {
    const r = fold(empty);
    expect([...r.total_tests]).toEqual([18, 8]);
    expect(r.avg_download[0]).toBeCloseTo(970 / 18, 4); // Σ(dl·tests)/Σtests
    expect(r.avg_download[1]).toBeCloseTo(20, 4);
    expect(r.apex_share[0]).toBeCloseTo(15 / 18, 4);
    expect(r.apex_share[1]).toBeCloseTo(0, 4); // no apex facts → 0 of the whole
    expect([...r.dominant_operator]).toEqual([0, 1]); // apex, zenith (codes into the domain)
  });

  test('perFact equality context folds to the surviving facts; an emptied mark is unknown', () => {
    const r = fold({ equals: { operator: 'apex' }, ranges: {} });
    expect(r.total_tests[0]).toBe(15); // m0 apex facts only
    expect(r.total_tests[1]).toBeNaN(); // m1 has no apex fact → empty → unknown
    expect(r.dominant_operator[0]).toBe(0); // apex
    expect(r.dominant_operator[1]).toBe(ARGMAX_UNKNOWN);
  });

  test('temporal range context restricts the fold to the selected bins', () => {
    const r = fold({ equals: {}, ranges: { quarter: { from: '2025-01-01', to: '2025-01-01' } } });
    expect([...r.total_tests]).toEqual([13, 8]); // m0 drops its 2025-04-01 apex fact (5)
    expect(r.dominant_operator[0]).toBe(0); // apex 10 still beats zenith 3
  });

  test('a selection outside the companion dictionary empties every mark', () => {
    const r = fold({ equals: { operator: 'nimbus' }, ranges: {} });
    expect(r.total_tests[0]).toBeNaN();
    expect(r.total_tests[1]).toBeNaN();
    expect([...r.dominant_operator]).toEqual([ARGMAX_UNKNOWN, ARGMAX_UNKNOWN]);
  });
});
