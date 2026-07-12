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
