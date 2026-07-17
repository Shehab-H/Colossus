// Client remote fold executor (companion-scale R4). The counterpart to the local foldTile/foldSlab: same
// seam, same output types (Float32Array per numeric measure; Uint16Array of canonical codes per
// argmax/argmin), same fold-result cache keys — only the executor moved. An over-budget view (priced
// `remote` at bake, or forced with `?fold=remote`) posts its context + on-screen tile keys and gets folded
// columns back (~marks × measures × 4 B) instead of fetching companion planes. Tiles themselves are still
// immutable static files (RULES R7); this endpoint is additive.

import { tableFromIPC } from 'apache-arrow';
import { API_BASE, type Manifest } from './manifest';
import type { FoldContext } from './measures';

export type FoldedColumns = Record<string, Float32Array | Uint16Array>;

export interface RemoteFoldResult {
  byTile: Map<string, FoldedColumns>;
  /** Wire bytes of the folded columns — what the remote route transfers per interaction. */
  responseBytes: number;
  /** Server-side fold compute ms (X-Fold-Ms), or null when the header isn't exposed. */
  serverMs: number | null;
}

/** `?fold=remote` forces the remote route, `?fold=client` (or `local`) forces the local fold — the R4
 *  benchmark/testing override. Absent ⇒ obey the manifest's bake-priced route. */
export function foldRouteOverride(search?: string): 'client' | 'remote' | null {
  const s = search ?? (typeof window === 'undefined' ? '' : window.location.search);
  const v = new URLSearchParams(s).get('fold');
  if (v === 'remote') return 'remote';
  if (v === 'client' || v === 'local') return 'client';
  return null;
}

/** Whether this manifest's folds execute remotely: the URL override wins, else the bake-priced route. */
export const isRemoteFold = (m: Manifest, override = foldRouteOverride()): boolean =>
  (override ?? m.foldRoute?.execution) === 'remote';

/** Fold a viewport's tiles on the server, batched in one request. The response carries only the measure
 *  columns (concatenated in tile order) plus a `tiles` directory in the Arrow schema metadata — one
 *  [tileKey, markCount] run per tile, mki-ordered — so each tile's columns are a zero-copy subarray and
 *  there is no per-row tile column on the wire. */
export async function foldRemote(
  viewId: string,
  version: string,
  measureNames: string[],
  ctx: FoldContext,
  tiles: string[],
  signal?: AbortSignal,
): Promise<RemoteFoldResult> {
  const res = await fetch(`${API_BASE}/views/${encodeURIComponent(viewId)}/fold`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ version, measures: measureNames, context: ctx, tiles }),
    signal,
  });
  if (!res.ok) throw new Error(`fold ${res.status} ${await res.text().catch(() => '')}`);
  const bytes = new Uint8Array(await res.arrayBuffer());
  const serverMs = res.headers.get('x-fold-ms');
  return { ...decodeFoldResponse(bytes, measureNames), responseBytes: bytes.byteLength, serverMs: serverMs ? +serverMs : null };
}

/** Decode the folded-columns Arrow into per-tile, mki-indexed columns. Exported for the benchmark harness
 *  and the parity test. */
export function decodeFoldResponse(bytes: Uint8Array, measureNames: string[]): { byTile: Map<string, FoldedColumns> } {
  const table = tableFromIPC(bytes);
  const dir = JSON.parse(table.schema.metadata.get('tiles') ?? '[]') as [string, number][];
  const cols: FoldedColumns = {};
  for (const name of measureNames) {
    const child = table.getChild(name);
    if (!child) throw new Error(`fold response missing measure '${name}'`);
    cols[name] = child.toArray() as Float32Array | Uint16Array;
  }
  const byTile = new Map<string, FoldedColumns>();
  let offset = 0;
  for (const [key, count] of dir) {
    const tileCols: FoldedColumns = {};
    for (const name of measureNames) tileCols[name] = cols[name].subarray(offset, offset + count) as Float32Array | Uint16Array;
    byTile.set(key, tileCols);
    offset += count;
  }
  return { byTile };
}
