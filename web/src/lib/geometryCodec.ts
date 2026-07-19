// Tile format 3 geometry decoder — the exact inverse of the bake-side C# GeometryCodec (see
// src/Colossus.Infrastructure/Tiles/GeometryCodec.cs), pinned cross-language by the shared fixtures in
// tests/fixtures. A polygon tile's geometry — ~69–99% of its bytes in format 2, and mechanically derivable —
// ships as one self-describing binary payload; this reconstructs the exact format-2 buffers (polyPositions,
// polyStartIndices, tile-global polyTriangles) bit-for-bit. Lossless by construction: every step below is the
// inverse of a reversible integer transform, so the synthesized floats equal the baked floats exactly.

/** The format-2 geometry buffers a tile decodes to — identical to what decodeTileV2 reads from the Arrow
 *  geometry/triangles columns, so decodeTileV3 is a drop-in producing the same TileData. */
export interface DecodedGeometry {
  positions: Float32Array; // flat interleaved [x0,y0,x1,y1,…] — deck's polyPositions
  startIndices: Uint32Array; // per-row vertex offset, length count+1
  triangles: Uint32Array; // tile-global triangle indices
}

const CODEC_RECT = 1;
const CODEC_DELTA = 2;

export function decodeGeometry(blob: Uint8Array): DecodedGeometry {
  const r = new Reader(blob);
  const codec = r.u8();
  r.u8(); // version
  if (codec === CODEC_RECT) return decodeRect(r);
  if (codec === CODEC_DELTA) return decodeDelta(r);
  throw new Error(`format 3: unknown geometry codec ${codec}`);
}

// rect: every row is an axis-aligned rectangle — a 1-byte template id (its vertex order) + four u16
// corner-table indices per row. A tile may carry a few templates (mixed source/aggregate windings). The
// triangle pattern is per template. Geometry, offsets and triangles are all synthesized.
function decodeRect(r: Reader): DecodedGeometry {
  const count = r.u32();
  const vertexCount = r.u32();
  const vertsPerRect = r.u8();
  const templateCount = r.u8();
  const triPat: Int32Array[] = new Array(templateCount);
  const xSel: Uint8Array[] = new Array(templateCount);
  const ySel: Uint8Array[] = new Array(templateCount);
  for (let t = 0; t < templateCount; t++) {
    const triLen = r.u8();
    const pat = new Int32Array(triLen);
    for (let i = 0; i < triLen; i++) pat[i] = r.u8();
    triPat[t] = pat;
    const xs = new Uint8Array(vertsPerRect);
    for (let v = 0; v < vertsPerRect; v++) xs[v] = r.u8();
    xSel[t] = xs;
    const ys = new Uint8Array(vertsPerRect);
    for (let v = 0; v < vertsPerRect; v++) ys[v] = r.u8();
    ySel[t] = ys;
  }
  const xTable = r.f32Array(r.u16());
  const yTable = r.f32Array(r.u16());

  const perRowTemplate = templateCount > 1;
  const positions = new Float32Array(2 * vertexCount);
  const start = new Uint32Array(count + 1);
  const tris: number[] = [];
  let fo = 0;
  for (let i = 0; i < count; i++) {
    const tid = perRowTemplate ? r.u8() : 0;
    const loX = xTable[r.u16()];
    const hiX = xTable[r.u16()];
    const loY = yTable[r.u16()];
    const hiY = yTable[r.u16()];
    const vb = i * vertsPerRect;
    const xs = xSel[tid];
    const ys = ySel[tid];
    for (let v = 0; v < vertsPerRect; v++) {
      positions[fo++] = xs[v] === 0 ? loX : hiX;
      positions[fo++] = ys[v] === 0 ? loY : hiY;
    }
    const pat = triPat[tid];
    for (let t = 0; t < pat.length; t++) tris.push(pat[t] + vb);
    start[i + 1] = vb + vertsPerRect;
  }
  return { positions, startIndices: start, triangles: Uint32Array.from(tris) };
}

// delta: de-interleaved x/y f32-bit streams, integer-delta + zigzag + byte-transposed; row-local triangle
// indices at minimal width whose per-row boundaries derive from part_offsets (no stored triangle offsets).
function decodeDelta(r: Reader): DecodedGeometry {
  const count = r.u32();
  const vertexCount = r.u32();
  const triWidth = r.u8();

  const numParts = new Int32Array(count);
  for (let i = 0; i < count; i++) numParts[i] = r.u16();
  const parts: Int32Array[] = new Array(count);
  for (let i = 0; i < count; i++) {
    const p = new Int32Array(numParts[i]);
    for (let j = 0; j < numParts[i]; j++) p[j] = r.u32() | 0;
    parts[i] = p;
  }

  if (triWidth !== 1 && triWidth !== 2 && triWidth !== 4)
    throw new Error(`format 3: unknown triangle index width ${triWidth}`);
  const triTotal = r.u32();
  const triLocal = new Int32Array(triTotal);
  for (let t = 0; t < triTotal; t++)
    triLocal[t] = triWidth === 1 ? r.u8() : triWidth === 2 ? r.u16() : r.u32();

  const xu = inverseByteTransposedZigzagDelta(r.take(4 * vertexCount), vertexCount);
  const yu = inverseByteTransposedZigzagDelta(r.take(4 * vertexCount), vertexCount);
  const xf = new Float32Array(xu.buffer); // reinterpret the u32 bits as f32 (bit-exact)
  const yf = new Float32Array(yu.buffer);

  const positions = new Float32Array(2 * vertexCount);
  for (let k = 0; k < vertexCount; k++) {
    positions[2 * k] = xf[k];
    positions[2 * k + 1] = yf[k];
  }

  const start = new Uint32Array(count + 1);
  for (let i = 0; i < count; i++) {
    const rowVerts = numParts[i] >= 2 ? parts[i][numParts[i] - 1] : 0;
    start[i + 1] = start[i] + rowVerts;
  }
  if (start[count] !== vertexCount)
    throw new Error(`format 3: part offsets span ${start[count]} vertices != declared ${vertexCount}`);

  const tris: number[] = [];
  let cursor = 0;
  for (let i = 0; i < count; i++) {
    const rowTriIdx = 3 * rowTriangleCount(parts[i], positions, start[i], start[i + 1] - start[i]);
    for (let t = 0; t < rowTriIdx; t++) tris.push(triLocal[cursor + t] + start[i]);
    cursor += rowTriIdx;
  }
  if (cursor !== triTotal)
    throw new Error(`format 3: derived triangle count ${cursor} != stored ${triTotal}`);
  return { positions, startIndices: start, triangles: Uint32Array.from(tris) };
}

// Mirror of the C# ear-clipper's deterministic per-row triangle count: a simple ring of m unique vertices
// yields m−2 triangles (0 if m < 3), summed over the row's parts. The closure test reads the reconstructed
// (bit-exact) positions, so it agrees with what the bake saw. The part end is clamped to the row's vertex
// count exactly as the encoder (and PolygonTriangulator) clamp it, so malformed offsets can never make the
// two sides slice the triangle stream differently.
function rowTriangleCount(
  parts: Int32Array,
  positions: Float32Array,
  vertexStart: number,
  rowVerts: number,
): number {
  if (parts.length < 2) return 0;
  let tris = 0;
  for (let q = 0; q + 1 < parts.length; q++) {
    const s = parts[q];
    const e = Math.min(parts[q + 1], rowVerts);
    let m = e - s;
    if (m >= 2) {
      const a = 2 * (vertexStart + s);
      const b = 2 * (vertexStart + e - 1);
      if (positions[a] === positions[b] && positions[a + 1] === positions[b + 1]) m--;
    }
    if (m >= 3) tris += m - 2;
  }
  return tris;
}

function inverseByteTransposedZigzagDelta(planes: Uint8Array, n: number): Uint32Array {
  const v = new Uint32Array(n);
  let prev = 0;
  for (let k = 0; k < n; k++) {
    const z = (planes[k] | (planes[n + k] << 8) | (planes[2 * n + k] << 16) | (planes[3 * n + k] << 24)) >>> 0;
    const d = ((z >>> 1) ^ -(z & 1)) >>> 0; // inverse zigzag
    prev = (prev + d) >>> 0; // wraps mod 2^32
    v[k] = prev;
  }
  return v;
}

// Little-endian reader over the payload (byte-for-byte the same layout the C# writer emits).
class Reader {
  private p = 0;
  private readonly buf: Uint8Array;
  private readonly dv: DataView;
  constructor(buf: Uint8Array) {
    this.buf = buf;
    this.dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  }
  u8(): number {
    return this.buf[this.p++];
  }
  u16(): number {
    const v = this.dv.getUint16(this.p, true);
    this.p += 2;
    return v;
  }
  u32(): number {
    const v = this.dv.getUint32(this.p, true);
    this.p += 4;
    return v >>> 0;
  }
  f32Array(n: number): Float32Array {
    const out = new Float32Array(n);
    for (let i = 0; i < n; i++) {
      out[i] = this.dv.getFloat32(this.p, true);
      this.p += 4;
    }
    return out;
  }
  take(n: number): Uint8Array {
    const s = this.buf.subarray(this.p, this.p + n);
    this.p += n;
    return s;
  }
}
