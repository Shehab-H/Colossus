// zstd block decode for the companion pack (companion-scale Work Item C). A slab bake compresses its blocks
// with high-level zstd + one trained shared dictionary per (view, version) — smaller than gzip on the small
// sliced cell-row blocks (SLAB-FORMAT §5). The browser's DecompressionStream has no zstd (and none with
// dictionary support), so a small (~250 KB) WASM decoder is instantiated ONCE per tile worker and the
// dictionary fetched ONCE per (view, version); both are cached in this module's state, which is per-worker.
import { createDCtx, decompress, decompressUsingDict, init } from '@bokuweb/zstd-wasm';

let ready: Promise<void> | null = null;
let dctx = 0;
const dicts = new Map<string, Promise<Uint8Array>>();

/** Instantiate the WASM decoder once (idempotent). init() fetches the bundled `zstd.wasm` — Vite resolves
 *  the `new URL('./zstd.wasm', import.meta.url)` inside the package, so it works in a worker with no config. */
function ready_(): Promise<void> {
  if (!ready) ready = init().then(() => { dctx = createDCtx(); });
  return ready;
}

function loadDict(url: string): Promise<Uint8Array> {
  let p = dicts.get(url);
  if (!p) {
    p = fetch(url)
      .then((r) => {
        if (!r.ok) throw new Error(`dict ${r.status} ${url}`);
        return r.arrayBuffer();
      })
      .then((b) => new Uint8Array(b));
    dicts.set(url, p);
  }
  return p;
}

/** Decompress one zstd block (or a dense plane region of concatenated frames), using the (view, version)
 *  dictionary when the bake trained one. Returns a fresh ArrayBuffer. */
export async function zstdDecompress(block: ArrayBuffer, dictUrl?: string): Promise<ArrayBuffer> {
  await ready_();
  const src = new Uint8Array(block);
  const out = dictUrl ? decompressUsingDict(dctx, src, await loadDict(dictUrl)) : decompress(src);
  // `out` is already copied out of WASM memory by the decoder; re-copy into a plain ArrayBuffer so callers
  // (tableFromIPC, Float32Array views, worker transfer) own a clean, exactly-sized buffer.
  const copy = new Uint8Array(out.byteLength);
  copy.set(out);
  return copy.buffer;
}
