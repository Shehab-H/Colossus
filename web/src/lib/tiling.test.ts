import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import type { Bbox } from './manifest';
import { coverTiles, pointToTile, prefetchCandidates, tileKey, tileRect } from './tiling';

// The shared cross-language tiling fixture — the SAME file the C# TileSql/TileMath tests verify. If the
// TS tiling math ever drifts from the bake's, this fails. Read at runtime (Node) so the browser build
// never sees it.
interface Fixture {
  root: { minX: number; minY: number; maxX: number; maxY: number };
  cases: { z: number; px: number; py: number; tileX: number; tileY: number }[];
}
const fixture: Fixture = JSON.parse(
  readFileSync(new URL('../../../tests/fixtures/tiling-cases.json', import.meta.url), 'utf8'),
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

describe('coverTiles', () => {
  const has = (keys: string[]) => (k: string) => keys.includes(k);

  test('a loaded desired tile covers itself', () => {
    expect(coverTiles([tileKey(2, 1, 1)], has([tileKey(2, 1, 1)]))).toEqual([tileKey(2, 1, 1)]);
  });

  test('a missing tile falls back to its nearest loaded ancestor', () => {
    expect(coverTiles([tileKey(3, 4, 4)], has([tileKey(1, 1, 1)]))).toEqual([tileKey(1, 1, 1)]);
  });

  test('a parent holds the screen until all four children are loaded (no partial quad)', () => {
    const parent = tileKey(0, 0, 0);
    const children = [tileKey(1, 0, 0), tileKey(1, 1, 0), tileKey(1, 0, 1), tileKey(1, 1, 1)];
    // Zoom-in: children desired. Only 3 of 4 loaded + parent loaded → parent holds, no partial quad.
    const partial = coverTiles(children, has([parent, ...children.slice(0, 3)]));
    expect(partial).toEqual([parent]);
    // All four loaded → the full child quad draws and the parent drops out.
    const full = coverTiles(children, has([parent, ...children]));
    expect(new Set(full)).toEqual(new Set(children));
  });
});

describe('prefetchCandidates', () => {
  const present: [number, number, number, boolean][] = [
    [0, 0, 0, false],
    [1, 0, 0, false], [1, 1, 0, true], [1, 0, 1, true], [1, 1, 1, true],
    [2, 0, 0, true], [2, 1, 0, true], [2, 0, 1, true], [2, 1, 1, true],
  ];
  const manifest = { tiles: present.map(([z, x, y, isLeaf]) => ({ z, x, y, count: 1, isLeaf })) } as unknown as Manifest;
  const keyset = new Set(present.map(([z, x, y]) => tileKey(z, x, y)));

  test('warms parents, the pan ring, and children — only baked tiles, never the selection', () => {
    const c = prefetchCandidates(manifest, ['1/0/0']);
    expect(c).toContain('0/0/0'); // parent (zoom-out)
    expect(c).toEqual(expect.arrayContaining(['1/1/0', '1/0/1', '1/1/1'])); // pan ring at this level
    expect(c).toEqual(expect.arrayContaining(['2/0/0', '2/1/0', '2/0/1', '2/1/1'])); // children (zoom-in)
    expect(c).not.toContain('1/0/0'); // never the selection itself
    expect(c.every((k) => keyset.has(k))).toBe(true); // only tiles the manifest baked
  });

  test('excludes tiles the manifest did not bake (out-of-bounds ring neighbours)', () => {
    expect(prefetchCandidates(manifest, ['1/1/1'])).not.toContain('1/2/1');
  });

  test('respects the cap', () => {
    expect(prefetchCandidates(manifest, ['1/0/0'], 2)).toHaveLength(2);
  });
});
