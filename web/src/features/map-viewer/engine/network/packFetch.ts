import type { PackCodec, RenderPack, CompanionPack } from '@/types/manifest';
import { record } from '@/lib/perf/recorder';
import { netMeasure } from '@/lib/perf/netMeasure';
import { SW_CACHE_HEADER } from '@/lib/perf/events';
import { inflate } from './inflate';
import { packBlockUrl, packDictUrl } from './tileUrls';
import { tableFromBlock } from './arrowFetch';
import type { Table } from 'apache-arrow';

/** Fetch a slab tile's requested planes out of the pack (companion-scale R1/R5 plane split). The wanted
 *  plane ranges are coalesced into contiguous runs — a fold that needs only some planes ranges only those
 *  bytes; a fold that needs them all ranges the whole tile region in one request. Each run is one HTTP
 *  Range keyed by `&r=<off>-<len>`: that offset+length pair is the distinct service-worker cache entry, and
 *  it must carry the length — a subset run and a superset run can start at the same offset (a tile first
 *  fetched under different colour measures), so offset alone would alias them to one cached body. Each gzip
 *  block within a run inflates independently. Returns `planeName → decompressed Arrow IPC bytes`. */
export const fetchSlabPlanes = (
  baseUrl: string,
  codec: PackCodec,
  dir: Record<string, [number, number]>,
  want: string[],
  signal?: AbortSignal,
  dictUrl?: string,
): Promise<Record<string, ArrayBuffer>> =>
  fetchPackBlocks(baseUrl, codec, dir, want, { signal, dictUrl, stage: 'net.facts' });

/** One tile's requested blocks out of a pack archive, coalesced into contiguous runs. Shared by the
 *  companion planes (above) and the render pack: both are "range these named blocks out of one archive and
 *  inflate each independently". `stage` picks which network budget the bytes are reported against —
 *  render-tile bytes reported as `net.facts` would silently vanish from the tile budget (see lib/perf). */
export async function fetchPackBlocks(
  baseUrl: string,
  codec: PackCodec,
  dir: Record<string, [number, number]>,
  want: string[],
  opts?: { signal?: AbortSignal; dictUrl?: string; stage?: 'net.tile' | 'net.facts' },
): Promise<Record<string, ArrayBuffer>> {
  const { signal, dictUrl, stage = 'net.facts' } = opts ?? {};
  const members = want
    .filter((p) => dir[p])
    .map((p) => ({ p, off: dir[p][0], len: dir[p][1] }))
    .sort((a, b) => a.off - b.off);

  const runs: { off: number; len: number; members: typeof members }[] = [];
  for (const m of members) {
    const last = runs.at(-1);
    if (last && m.off === last.off + last.len) {
      last.len += m.len;
      last.members.push(m);
    } else runs.push({ off: m.off, len: m.len, members: [m] });
  }

  const out: Record<string, ArrayBuffer> = {};
  await Promise.all(
    runs.map(async (run) => {
      const url = `${baseUrl}&r=${run.off}-${run.len}`;
      const t0 = performance.now();
      const res = await fetch(url, { signal, headers: { Range: `bytes=${run.off}-${run.off + run.len - 1}` } });
      if (!res.ok) throw new Error(`companion ${res.status} ${url}`);
      const body = await res.arrayBuffer();
      // One event per coalesced run — the unit actually requested. `bytes` is the block bytes received
      // (still compressed; the inflate stage reports what they expand to); `wire`/`cached` come from
      // Resource Timing, so a service-worker hit on a warm pan reports 0 rather than the run length.
      record({
        stage,
        ms: performance.now() - t0,
        t: performance.now(),
        bytes: body.byteLength,
        n: run.members.length,
        ...netMeasure(url, body.byteLength, res.headers.get(SW_CACHE_HEADER)),
      });
      const whole = body.byteLength !== run.len; // server ignored Range → whole archive
      await Promise.all(
        run.members.map(async (m) => {
          const start = whole ? m.off : m.off - run.off;
          out[m.p] = await inflate(body.slice(start, start + m.len), codec, dictUrl);
        }),
      );
    }),
  );
  return out;
}

/** Fetch a **dense** tile's requested cell rows (companion-scale R5 cell-run slicing). `needs` names the cell
 *  indices wanted per plane; `sliceDir` gives each plane's per-cell-row compressed block lengths and `dir`
 *  each plane's base offset (`planeEntries[tile][plane][0]`), so cell `c`'s block is at `base + Σ_{i<c} len_i`
 *  for `len_c` bytes. All wanted (plane, cell) blocks are collected, coalesced into contiguous byte runs
 *  across planes, each run one HTTP Range keyed by `&r=off-len` (its service-worker entry), and every raw
 *  cell-row block within a run inflated independently — dense blocks are raw little-endian, not Arrow, so the
 *  browser never decodes a concatenated-gzip stream. Returns `plane → {cell, buf}[]` (an entry per wanted
 *  plane, empty when a context needs none of its cells). */
export async function fetchSlabCellRows(
  baseUrl: string,
  codec: PackCodec,
  dir: Record<string, [number, number]>,
  sliceDir: Record<string, number[]>,
  needs: Record<string, number[]>,
  signal?: AbortSignal,
  dictUrl?: string,
): Promise<Record<string, { cell: number; buf: ArrayBuffer }[]>> {
  const out: Record<string, { cell: number; buf: ArrayBuffer }[]> = {};
  const members: { p: string; cell: number; off: number; len: number }[] = [];
  for (const p of Object.keys(needs)) {
    out[p] = [];
    const base = dir[p]?.[0];
    const lens = sliceDir[p];
    if (base === undefined || !lens) continue;
    const wanted = [...new Set(needs[p])].sort((a, b) => a - b);
    let wi = 0;
    let off = base; // prefix-sum lens: cell c starts at base + Σ_{i<c} lens[i]
    for (let c = 0; c < lens.length && wi < wanted.length; c++) {
      if (c === wanted[wi]) { members.push({ p, cell: c, off, len: lens[c] }); wi++; }
      off += lens[c];
    }
  }
  members.sort((a, b) => a.off - b.off);

  const runs: { off: number; len: number; members: typeof members }[] = [];
  for (const m of members) {
    const last = runs.at(-1);
    if (last && m.off === last.off + last.len) { last.len += m.len; last.members.push(m); }
    else runs.push({ off: m.off, len: m.len, members: [m] });
  }

  await Promise.all(
    runs.map(async (run) => {
      const url = `${baseUrl}&r=${run.off}-${run.len}`;
      const t0 = performance.now();
      const res = await fetch(url, { signal, headers: { Range: `bytes=${run.off}-${run.off + run.len - 1}` } });
      if (!res.ok) throw new Error(`companion ${res.status} ${url}`);
      const body = await res.arrayBuffer();
      record({ stage: 'net.facts', ms: performance.now() - t0, t: performance.now(), bytes: body.byteLength, n: run.members.length, ...netMeasure(url, body.byteLength, res.headers.get(SW_CACHE_HEADER)) });
      const whole = body.byteLength !== run.len; // server ignored Range → whole archive
      await Promise.all(
        run.members.map(async (m) => {
          const start = whole ? m.off : m.off - run.off;
          out[m.p].push({ cell: m.cell, buf: await inflate(body.slice(start, start + m.len), codec, dictUrl) });
        }),
      );
    }),
  );
  return out;
}
/** How to fetch a packed tile's blocks, resolved on the main thread (the manifest lives there) and
 *  shipped to the worker per request — the render-pack counterpart of CompanionFetch. `want` is the group
 *  set to range: the manifest's firstPaint run for an initial load, or a single channel for a lazy fetch. */
export interface RenderFetch {
  baseUrl: string;
  codec: PackCodec;
  dictUrl?: string;
  /** This tile's group directory: group -> [offset, length]. */
  dir: Record<string, [number, number]>;
  want: string[];
}

/** Resolve a tile's render-pack fetch spec, or null when this bake is not packed (older bakes, formats
 *  1/2) — null routes to the per-tile file path. `want` defaults to the manifest's first-paint run, which
 *  the writer laid down contiguously, so the fetch is one coalesced range request. */
export function renderFetch(
  pack: RenderPack | undefined,
  viewId: string,
  version: string,
  key: string,
  want?: string[],
): RenderFetch | null {
  const dir = pack?.entries[key];
  if (!pack || !dir) return null;
  return {
    baseUrl: packBlockUrl(viewId, version, pack.file, key),
    codec: pack.codec,
    dictUrl: packDictUrl(viewId, version, pack),
    dir,
    want: (want ?? pack.firstPaint).filter((g) => g in dir),
  };
}

/** Fetch + inflate + parse a packed tile's requested groups. Adjacent blocks coalesce into one range
 *  request, so the default first-paint set costs a single HTTP request per tile. */
export async function fetchBlocks(
  spec: RenderFetch,
  signal?: AbortSignal,
): Promise<{ tables: Record<string, Table>; buffers: Record<string, ArrayBuffer> }> {
  const raw = await fetchPackBlocks(spec.baseUrl, spec.codec, spec.dir, spec.want, {
    signal,
    dictUrl: spec.dictUrl,
    stage: 'net.tile',
  });
  const tables: Record<string, Table> = {};
  const buffers: Record<string, ArrayBuffer> = {};
  for (const [group, buf] of Object.entries(raw)) {
    tables[group] = tableFromBlock(buf).table;
    buffers[group] = buf;
  }
  return { tables, buffers };
}

/** One leaf tile's block in the companion pack — where to range-read it and how to decompress it.
 *  Resolved on the main thread (the manifest lives there) and shipped to the worker per request. */
export interface PackBlock {
  url: string;
  offset: number;
  length: number;
  codec: PackCodec;
}

/** The pack block for a tile, or null when this tile isn't packed (internal level, older bake, or a
 *  leaf that baked no companion) — null routes to the per-file companion fetch. */
export function packBlock(
  pack: CompanionPack | undefined,
  viewId: string,
  version: string,
  key: string,
): PackBlock | null {
  const entry = pack?.entries[key];
  if (!pack || !entry) return null;
  return { url: packBlockUrl(viewId, version, pack.file, key), offset: entry[0], length: entry[1], codec: pack.codec };
}
