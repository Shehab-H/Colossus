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

const inflate = (block: ArrayBuffer, codec: CompressionFormat): Promise<ArrayBuffer> =>
  new Response(new Response(block).body!.pipeThrough(new DecompressionStream(codec))).arrayBuffer();

/** Fetch a slab tile's requested planes out of the pack (companion-scale R1/R5 plane split). The wanted
 *  plane ranges are coalesced into contiguous runs — a fold that needs only some planes ranges only those
 *  bytes; a fold that needs them all ranges the whole tile region in one request. Each run is one HTTP
 *  Range keyed by `&r=<off>-<len>`: that offset+length pair is the distinct service-worker cache entry, and
 *  it must carry the length — a subset run and a superset run can start at the same offset (a tile first
 *  fetched under different colour measures), so offset alone would alias them to one cached body. Each gzip
 *  block within a run inflates independently. Returns `planeName → decompressed Arrow IPC bytes`. */
export async function fetchSlabPlanes(
  baseUrl: string,
  codec: CompressionFormat,
  dir: Record<string, [number, number]>,
  want: string[],
  signal?: AbortSignal,
): Promise<Record<string, ArrayBuffer>> {
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
      const res = await fetch(url, { signal, headers: { Range: `bytes=${run.off}-${run.off + run.len - 1}` } });
      if (!res.ok) throw new Error(`companion ${res.status} ${url}`);
      const body = await res.arrayBuffer();
      const whole = body.byteLength !== run.len; // server ignored Range → whole archive
      await Promise.all(
        run.members.map(async (m) => {
          const start = whole ? m.off : m.off - run.off;
          out[m.p] = await inflate(body.slice(start, start + m.len), codec);
        }),
      );
    }),
  );
  return out;
}
