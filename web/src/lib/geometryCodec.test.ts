import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { decodeGeometry } from './geometryCodec';

// Cross-language authority: the same fixture the C# GeometryFixtureTests pins the encoder/decoder against
// (tests/fixtures/geometry-codec-cases.json). Decoding the committed payload bytes here must land on the
// exact same format-2 buffers — a format drift fails both suites.
interface Case {
  name: string;
  codec: number;
  payloadBase64: string;
  positions: number[];
  startIndices: number[];
  triangles: number[];
}
const fixture: { cases: Case[] } = JSON.parse(
  readFileSync(new URL('../../../tests/fixtures/geometry-codec-cases.json', import.meta.url), 'utf8'),
);

const fromBase64 = (b64: string): Uint8Array =>
  Uint8Array.from(Buffer.from(b64, 'base64'));

describe('geometryCodec format 3', () => {
  for (const c of fixture.cases) {
    it(`decodes ${c.name} (codec ${c.codec}) bit-for-bit`, () => {
      const g = decodeGeometry(fromBase64(c.payloadBase64));
      // Wrap expected coords in Float32Array so the comparison is at f32 precision (the shortest-roundtrip
      // JSON number for each baked float rounds back to that exact float).
      expect(Array.from(g.positions)).toEqual(Array.from(Float32Array.from(c.positions)));
      expect(Array.from(g.startIndices)).toEqual(c.startIndices);
      expect(Array.from(g.triangles)).toEqual(c.triangles);
    });
  }

  it('covers both codecs', () => {
    const codecs = new Set(fixture.cases.map((c) => c.codec));
    expect(codecs.has(1)).toBe(true); // rect
    expect(codecs.has(2)).toBe(true); // delta
  });
});
