import { tableFromIPC, type Table } from 'apache-arrow';

/** A parsed tile plus the one ArrayBuffer it was parsed from. Under tile format 2 the table's column
 *  buffers are typed-array views into `buffer`, so retaining it lets decode take views instead of
 *  copies (see tileData decodeTile). Format-1 callers ignore `buffer`. */
export interface FetchedArrow {
  table: Table;
  buffer: ArrayBuffer;
}

// Tiles are Arrow IPC (stream format). Loading one is fetch → tableFromIPC: essentially a memcpy, no
// decode. The resulting column buffers ARE the typed arrays deck.gl wants, so a tile flows disk → GPU
// with no per-cell work and nothing to unwind on the JS heap — the whole reason we left Parquet behind.
export async function fetchArrowTable(url: string, signal?: AbortSignal): Promise<FetchedArrow> {
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`tile ${res.status} ${url}`);
  const buffer = await res.arrayBuffer();
  return { table: tableFromIPC(new Uint8Array(buffer)), buffer };
}

/** One tile's block out of a companion pack (companion-scale R2): HTTP-Range the compressed block,
 *  decompress with the browser-native DecompressionStream, parse the Arrow IPC inside. */
export async function fetchArrowBlock(
  url: string,
  offset: number,
  length: number,
  codec: CompressionFormat,
  signal?: AbortSignal,
): Promise<FetchedArrow> {
  const res = await fetch(url, { signal, headers: { Range: `bytes=${offset}-${offset + length - 1}` } });
  if (!res.ok) throw new Error(`tile ${res.status} ${url}`);
  const buffer = await inflateBlock(await res.arrayBuffer(), offset, length, codec);
  return { table: tableFromIPC(new Uint8Array(buffer)), buffer };
}

/** Decompress one block of a ranged response body. An exact-length body IS the block (a 206, or the
 *  service worker's cached copy); anything longer is the whole archive from a server that ignored
 *  Range, so the block is sliced out first — the slice must be exact, because a gzip member followed
 *  by trailing bytes (the next tile's block) fails DecompressionStream. */
export async function inflateBlock(
  body: ArrayBuffer,
  offset: number,
  length: number,
  codec: CompressionFormat,
): Promise<ArrayBuffer> {
  const block = body.byteLength === length ? body : body.slice(offset, offset + length);
  const inflated = new Response(block).body!.pipeThrough(new DecompressionStream(codec));
  return new Response(inflated).arrayBuffer();
}
