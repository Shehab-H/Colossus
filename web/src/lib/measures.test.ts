import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import { parseMeasure } from './measures';

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
