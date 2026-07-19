// Mirrors the baked manifest.json (camelCase, lowercase enums). The client reads the descriptor and
// obeys it — one code path renders every mark and viewport because only the config differs.

export type Viewport = 'geo' | 'orthographic';
export type Mark = 'point' | 'line' | 'arc' | 'rect' | 'polygon' | 'heat' | 'text';

export interface Bbox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface ChannelSpec {
  name: string;
  column: string;
  role: 'measure' | 'dimension' | 'temporal' | 'identity';
  type: string;
}

export interface GeometrySpec {
  kind: string;
}

export interface SourceSpec {
  adapter: string;
  query: string;
  geometry: GeometrySpec;
  channels: ChannelSpec[];
}

/** A data channel bound to a visual channel, optionally with a named scheme (e.g. color ramp). */
export interface ChannelRef {
  channel: string;
  scheme?: string;
}

export type ScaleType =
  | 'linear'
  | 'log'
  | 'sqrt'
  | 'diverging'
  | 'quantize'
  | 'quantile'
  | 'threshold'
  | 'ordinal'
  | 'categorical';

/** How a data channel maps to color. A superset scale spec (à la Vega-Lite): pick the `type`, a named
 *  `scheme` or explicit `range`, and the modifiers that type uses. Everything but `channel` is optional
 *  — the client infers a sensible scale from the channel's datatype when omitted. */
export interface ColorSpec {
  channel: string;
  type?: ScaleType;
  scheme?: string;
  range?: string[]; // explicit hex list — overrides scheme
  domain?: (number | string)[]; // numeric [min,max] or explicit category order
  reverse?: boolean;
  midpoint?: number; // diverging
  bins?: number; // quantize / quantile
  thresholds?: number[]; // threshold
  palette?: Record<string, string>; // categorical: explicit value → hex
  unknown?: string; // color for unmapped / null values
}

export interface EncodingSpec {
  color?: ColorSpec;
  size?: ChannelRef;
}

/** Click-to-inspect config. When absent, marks aren't pickable and clicks do nothing. */
export interface InspectSpec {
  channels: string[];
  title?: string;
}

/** A computed per-mark value (VIEW_CONFIG §4). Its presence opts the view into the group regime. */
export interface MeasureSpec {
  name: string;
  expr: string;
}

export interface ViewConfig {
  id: string;
  title?: string;
  viewport: Viewport;
  mark: Mark;
  reduction?: string;
  source: SourceSpec;
  measures?: MeasureSpec[];
  encoding?: EncodingSpec;
  inspect?: InspectSpec;
}

/** Group regime: the derived split of a view's channels. perMark channels ride the render tiles;
 *  perFact channels live only in the fact companions and drive the fold. */
export interface FactChannels {
  perMark: string[];
  perFact: string[];
}

export interface TileMeta {
  z: number;
  x: number;
  y: number;
  count: number;
  isLeaf: boolean;
}

/** One channel's data domain, scanned from the FULL extract at bake time (unlike the root tile, which
 *  is a sample). Numeric channels carry min/max + a quantile grid; others their distinct values —
 *  `valuesTruncated` marks a capped list, which the client treats as absent. */
export interface ChannelDomain {
  values?: string[];
  valuesTruncated?: boolean;
  min?: number;
  max?: number;
  quantiles?: number[];
}

export interface Manifest {
  version: string;
  /** Tile binary format. 2 = zero-copy (views over one buffer); absent/0/1 = the copy-based decode. */
  tileFormat?: number;
  view: ViewConfig;
  /** The reduction the bake planner chose (e.g. 'aggregate', 'quadtreeLod'). Drives the fidelity label. */
  reduction: string;
  regime: string;
  root: Bbox;
  minZoom: number;
  maxZoom: number;
  tilePointBudget: number;
  totalPoints: number;
  tiles: TileMeta[];
  /** Absent on manifests from older bakes — consumers fall back to scanning the root tile. */
  channelDomains?: Record<string, ChannelDomain>;
  /** Group regime only: the perMark/perFact split (routes a filter to a GPU predicate or the fold). */
  factChannels?: FactChannels;
  /** True when the bake wrote a `z/x/y.facts.arrow` companion beside every tile. */
  companionTiles?: boolean;
  /** The companion grain columns (perFact dict + temporal), the dimensions the fact partials key by. */
  grainChannels?: string[];
  /** Companion packaging (companion-scale R2, extended by R1/R5): companion tiles live as independently
   *  compressed blocks in one per-version archive, range-read per tile. Absent selects the per-file layout
   *  (older bakes). A slab bake packs both leaf and internal levels and fills `planeEntries`. */
  companionPack?: CompanionPack;
  /** Group-regime slab companion metadata (companion-scale R1). Present ⇒ the pack holds slab planes and
   *  the client takes the slab decode/fold; absent ⇒ the row-form companion path (older bakes). */
  companionSlab?: CompanionSlab;
  /** Group-regime only (companion-scale R4): the baked facts Parquet the server fold reads, relative to
   *  the version dir. The client never fetches it — it is the server's input. */
  factsParquet?: string;
  /** Group-regime only (companion-scale R4): the planner's fold-execution route, priced at bake. The
   *  client folds locally over companion planes when `client`, or posts to /api/views/{id}/fold when
   *  `remote` (behind the same seam). A `?fold=remote|client` query override wins over this. */
  foldRoute?: FoldRoute;
  /** Render-tile packaging (tile-transfer Phase 3): every tile's columns live as independently compressed
   *  blocks in one per-version archive, range-read per (tile, group), so a first paint ships geometry plus
   *  the active colour channel and never the measure planes it will not read. Absent selects the per-tile
   *  `z/x/y.arrow` path (formats 1/2, older bakes). Present ⇒ NO per-tile .arrow or .arrow.br exists. */
  renderPack?: RenderPack;
}

/** The render archive and its directory (mirror of Colossus.Domain.Model.RenderPack). `entries` maps a tile
 *  key (`z/x/y`) to each group's `[offset, length]`; a group is one column, except `@geom` which carries the
 *  encoded geometry payload (area marks) or the x/y pair (point marks). `firstPaint` is the group run a
 *  default paint needs, in pack order — the writer lays those blocks down adjacently at the head of each
 *  tile's span, so fetching exactly that set is ONE coalesced range request per tile. */
export interface RenderPack {
  file: string;
  codec: PackCodec;
  entries: Record<string, Record<string, [number, number]>>;
  firstPaint: string[];
  dict?: string;
  dictHash?: string;
}

/** The geometry group's reserved name — not a channel name (mirrors RenderPack.GeomGroup). */
export const GEOM_GROUP = '@geom';

/** The bake-priced fold-execution route (companion-scale R4). The client obeys `execution`; the rest are
 *  diagnostics (measured per-interaction bytes vs the budget). */
export interface FoldRoute {
  execution: 'client' | 'remote';
  worstTileBytes?: number;
  viewportEstimateBytes?: number;
  budgetBytes?: number;
  forced?: boolean;
}

/** One companion grain channel as a slab axis (SLAB-FORMAT §1–2). `domain` is the full ordered value list
 *  (categorical: canonical dict order; ordered: the sorted bin list). Axes are in cell order (fastest last). */
export interface SlabAxis {
  name: string;
  kind: 'categorical' | 'ordered';
  cardinality: number;
  cumulative: boolean;
  domain: string[];
}

/** Group-regime slab companion metadata (SLAB-FORMAT). `layout` is the view default; a tile's actual layout
 *  is `tileLayouts[key] ?? layout` (SLAB-FORMAT §3). `partials` names the planes and their element type. */
export interface CompanionSlab {
  layout: 'sparse' | 'dense';
  cells: number;
  occupancy: number;
  axes: SlabAxis[];
  partials: { name: string; type: 'f32' | 'i32' }[];
  /** Per-leaf-tile layout overrides: `tileKey → 'dense'|'sparse'` for tiles whose own occupancy disagrees
   *  with `layout`. Only exceptions are listed; absent tiles (and an absent field) use `layout`. */
  tileLayouts?: Record<string, 'sparse' | 'dense'>;
}

/** A tile's layout: its per-tile override if any, else the view default (SLAB-FORMAT §3). The one place the
 *  client resolves a per-tile choice — it must know a tile's layout before it fetches (sparse fetches the
 *  `@idx` structure, dense fetches `cnt`; the slice math differs). */
export function tileLayoutOf(slab: CompanionSlab, tileKey: string): 'sparse' | 'dense' {
  return slab.tileLayouts?.[tileKey] ?? slab.layout;
}

/** Companion pack block codec. "gzip" decodes with the browser-native `DecompressionStream`; "zstd" (a slab
 *  bake, Work Item C) with a WASM decoder + the pack's trained dictionary (compression lives inside the
 *  archive — a `Content-Encoding` wouldn't compose with range requests). */
export type PackCodec = 'gzip' | 'zstd';

/** The leaf companion archive and its directory: `file` is relative to the version directory, and `entries`
 *  maps a tile key `z/x/y` to its `[offset, length]` byte range. */
export interface CompanionPack {
  file: string;
  codec: PackCodec;
  entries: Record<string, [number, number]>;
  /** Trained shared dictionary path (relative to the version dir), for the "zstd" codec: fetched once per
   *  (view, version) and loaded into the client's zstd decoder. Absent for gzip, or a bake too small to train
   *  one (then zstd blocks decode dictionary-free). */
  dict?: string;
  /** SHA-256 (hex) of the dictionary file. */
  dictHash?: string;
  /** "row" (R2 leaf pack) or "slab" (R1). Absent ⇒ "row" (older bakes). */
  format?: 'row' | 'slab';
  /** Slab only: `tileKey → (planeName → [offset, length])`. Plane `"@idx"` is the CSR structure block;
   *  the rest are partial planes. A dense plane's region is its cell-row blocks concatenated. Enables
   *  plane-split fetch (R5); absent ⇒ whole-tile fetch. */
  planeEntries?: Record<string, Record<string, [number, number]>>;
  /** Cell-run slice directory (R5 second half): `tileKey → (planeName → per-cell-row block lengths)`, for
   *  **dense** tiles only (sparse opts out). A dense plane is one compressed block per cell row; cell `c`'s
   *  block is at `planeEntries[tile][plane][0] + Σ_{i<c} lengths[i]` for `lengths[c]` bytes, so only lengths
   *  are stored. Blocks are raw little-endian f32/i32 (per the partial), not Arrow. Absent (or a sparse tile)
   *  ⇒ whole-block fetch. */
  sliceEntries?: Record<string, Record<string, number[]>>;
}

const TILES_BASE = import.meta.env.VITE_TILES_BASE ?? 'http://localhost:5174/tiles';
export const API_BASE = import.meta.env.VITE_API_BASE ?? TILES_BASE.replace(/\/tiles\/?$/, '/api');

export function tileUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.arrow`;
}

/** The fact companion beside a group-regime render tile. */
export function factsUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.facts.arrow`;
}

/** The companion pack archive, tagged with the tile whose block a range request is after. Static
 *  servers ignore the query; it makes the URL — and so the service worker's cache key — per-tile. */
export function packBlockUrl(viewId: string, version: string, file: string, key: string): string {
  return `${TILES_BASE}/${viewId}/${version}/${file}?tile=${key}`;
}

/** The trained zstd dictionary URL for a (view, version) pack, or undefined when the bake trained none
 *  (gzip, or too few blocks). The client fetches it once and loads it into the worker's zstd decoder. */
export function packDictUrl(viewId: string, version: string, pack: { dict?: string } | undefined): string | undefined {
  return pack?.dict ? `${TILES_BASE}/${viewId}/${version}/${pack.dict}` : undefined;
}

/** Resolve latest.json → manifest.json for a view. */
export async function loadManifest(viewId: string): Promise<Manifest> {
  const pointer = await fetch(`${TILES_BASE}/${viewId}/latest.json`, { cache: 'no-cache' }).then((r) => r.json());
  const version: string = pointer.version;
  return fetch(`${TILES_BASE}/${viewId}/${version}/manifest.json`).then((r) => r.json());
}
