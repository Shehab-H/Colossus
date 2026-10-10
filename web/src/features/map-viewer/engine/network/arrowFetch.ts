import { record } from '@/lib/perf/recorder';
import { netMeasure } from '@/lib/perf/netMeasure';
import { SW_CACHE_HEADER } from '@/lib/perf/events';
import { timedSync } from '@/lib/perf/timing';
import type { PackCodec } from '@/types/manifest';
import { inflateBlock } from './inflate';
import { type Table, tableFromIPC } from 'apache-arrow';

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
  const t0 = performance.now();
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`tile ${res.status} ${url}`);
  const buffer = await res.arrayBuffer();
  record({
    stage: 'net.tile',
    ms: performance.now() - t0,
    t: performance.now(),
    bytes: buffer.byteLength,
    ...netMeasure(url, buffer.byteLength, res.headers.get(SW_CACHE_HEADER)),
  });
  const table = timedSync('decode.ipc', () => tableFromIPC(new Uint8Array(buffer)), (tb) => ({ n: tb.numRows, bytes: buffer.byteLength }));
  return { table, buffer };
}

/** One tile's block out of a companion pack (companion-scale R2): HTTP-Range the compressed block,
 *  decompress with the browser-native DecompressionStream, parse the Arrow IPC inside. */
export async function fetchArrowBlock(
  url: string,
  offset: number,
  length: number,
  codec: PackCodec,
  signal?: AbortSignal,
  dictUrl?: string,
): Promise<FetchedArrow> {
  const t0 = performance.now();
  const res = await fetch(url, { signal, headers: { Range: `bytes=${offset}-${offset + length - 1}` } });
  if (!res.ok) throw new Error(`tile ${res.status} ${url}`);
  const body = await res.arrayBuffer();
  record({
    stage: 'net.facts',
    ms: performance.now() - t0,
    t: performance.now(),
    bytes: body.byteLength,
    ...netMeasure(url, body.byteLength, res.headers.get(SW_CACHE_HEADER)),
  });
  const buffer = await inflateBlock(body, offset, length, codec, dictUrl);
  const table = timedSync('decode.ipc', () => tableFromIPC(new Uint8Array(buffer)), (tb) => ({ n: tb.numRows, bytes: buffer.byteLength }));
  return { table, buffer };
}

/** One inflated pack block parsed as its own single-batch Arrow table. The returned `buffer` IS that
 *  block's inflated buffer — the retention anchor for any column viewed out of this table, since a packed
 *  tile has one buffer per column rather than one per tile. */
export function tableFromBlock(buffer: ArrayBuffer): FetchedArrow {
  const table = timedSync('decode.ipc', () => tableFromIPC(new Uint8Array(buffer)), (tb) => ({
    n: tb.numRows,
    bytes: buffer.byteLength,
  }));
  return { table, buffer };
}
