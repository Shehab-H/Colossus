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

  // A row carrying more than 65,536 vertices makes the encoder widen the triangle stream to u32. Rather than
  // committing a multi-hundred-KB fixture for it, take the committed delta payload and re-emit only its
  // triangle stream at width 4 — every other byte, including the encoder's coordinate planes, is untouched,
  // so this pins the decoder's wide-index path against real encoder output. (The C# side round-trips an
  // actual >65,536-vertex row; see GeometryCodecTests.)
  it('decodes a u32-width triangle stream', () => {
    const src = fromBase64(fixture.cases.find((c) => c.name === 'delta-triangle')!.payloadBase64);
    const expected = decodeGeometry(src);
    const dv = new DataView(src.buffer, src.byteOffset, src.byteLength);

    const count = dv.getUint32(2, true);
    let p = 10;
    const width = src[p++];
    expect(width).toBe(1);
    const numParts: number[] = [];
    for (let i = 0; i < count; i++) {
      numParts.push(dv.getUint16(p, true));
      p += 2;
    }
    for (const n of numParts) p += 4 * n;
    const triTotal = dv.getUint32(p, true);
    p += 4;
    const triAt = p;
    const tailAt = triAt + triTotal * width;

    const wide = new Uint8Array(src.byteLength + triTotal * (4 - width));
    wide.set(src.subarray(0, tailAt - triTotal * width), 0);
    wide[10] = 4;
    const wdv = new DataView(wide.buffer);
    for (let t = 0; t < triTotal; t++) wdv.setUint32(triAt + 4 * t, src[triAt + t], true);
    wide.set(src.subarray(tailAt), triAt + 4 * triTotal);

    const got = decodeGeometry(wide);
    expect(Array.from(got.positions)).toEqual(Array.from(expected.positions));
    expect(Array.from(got.startIndices)).toEqual(Array.from(expected.startIndices));
    expect(Array.from(got.triangles)).toEqual(Array.from(expected.triangles));
  });

  it('rejects an unknown triangle index width', () => {
    const src = fromBase64(fixture.cases.find((c) => c.name === 'delta-triangle')!.payloadBase64);
    const bad = Uint8Array.from(src);
    bad[10] = 3;
    expect(() => decodeGeometry(bad)).toThrow(/unknown triangle index width/);
  });
});
