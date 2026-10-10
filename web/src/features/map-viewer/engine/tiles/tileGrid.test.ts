import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import { pointToTile, tileRect } from './tileGrid';
import type { Bbox } from '@/types/viewConfig';

// The shared cross-language tiling fixture — the SAME file the C# TileSql/TileMath tests verify. If the
// TS tiling math ever drifts from the bake's, this fails. Read at runtime (Node) so the browser build
// never sees it.
interface Fixture {
  root: { minX: number; minY: number; maxX: number; maxY: number };
  cases: { z: number; px: number; py: number; tileX: number; tileY: number }[];
}
const fixture: Fixture = JSON.parse(
  readFileSync(new URL('../../../../../../tests/fixtures/tiling-cases.json', import.meta.url), 'utf8'),
);
const root: Bbox = fixture.root;

describe('tiling conformance (shared fixture)', () => {
  test('pointToTile reproduces every fixture case', () => {
    expect(fixture.cases.length).toBeGreaterThan(0);
    for (const c of fixture.cases) {
      expect([c.px, c.py, pointToTile(root, c.z, c.px, c.py)]).toEqual([c.px, c.py, [c.tileX, c.tileY]]);
    }
  });

  test('tileRect is the inverse of pointToTile for interior points', () => {
    for (const c of fixture.cases) {
      // Skip clamped (out-of-range) cases: their point lies outside the edge tile's rect by design.
      const n = 2 ** c.z;
      const inRange =
        c.px >= root.minX && c.px < root.maxX && c.py >= root.minY && c.py < root.maxY && n > 0;
      if (!inRange) continue;
      const r = tileRect(root, c.z, c.tileX, c.tileY);
      expect(c.px).toBeGreaterThanOrEqual(r.xMin);
      expect(c.px).toBeLessThanOrEqual(r.xMax);
      expect(c.py).toBeGreaterThanOrEqual(r.yMin);
      expect(c.py).toBeLessThanOrEqual(r.yMax);
    }
  });
});

