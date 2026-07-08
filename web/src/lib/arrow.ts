import { tableFromIPC, type Table } from 'apache-arrow';

// Tiles are Arrow IPC (stream format). Loading one is fetch → tableFromIPC: essentially a memcpy, no
// decode. The resulting column buffers ARE the typed arrays deck.gl wants, so a tile flows disk → GPU
// with no per-cell work and nothing to unwind on the JS heap — the whole reason we left Parquet behind.
export async function fetchArrowTable(url: string): Promise<Table> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`tile ${res.status} ${url}`);
  return tableFromIPC(new Uint8Array(await res.arrayBuffer()));
}
