import type { PackCodec } from '@/types/manifest';
import { zstdDecompress } from './zstd';
import { record } from '@/lib/perf/recorder';

/** Decompress one block of a ranged response body. An exact-length body IS the block (a 206, or the
 *  service worker's cached copy); anything longer is the whole archive from a server that ignored
 *  Range, so the block is sliced out first — the slice must be exact, because a gzip member followed
 *  by trailing bytes (the next tile's block) fails DecompressionStream. */
export async function inflateBlock(
  body: ArrayBuffer,
  offset: number,
  length: number,
  codec: PackCodec,
  dictUrl?: string,
): Promise<ArrayBuffer> {
  const block = body.byteLength === length ? body : body.slice(offset, offset + length);
  return inflate(block, codec, dictUrl);
}

export const inflate = async (block: ArrayBuffer, codec: PackCodec, dictUrl?: string): Promise<ArrayBuffer> => {
  const t0 = performance.now();
  // zstd (slab bake, Work Item C) has no browser-native DecompressionStream — decode via the WASM decoder
  // and the (view, version) dictionary. gzip stays on the native stream (older bakes, row-form packs).
  const out =
    codec === 'zstd'
      ? await zstdDecompress(block, dictUrl)
      : await new Response(new Response(block).body!.pipeThrough(new DecompressionStream(codec))).arrayBuffer();
  // `bytes` is what the block expands to; the compressed size it came from is net.facts' own `bytes`.
  // No `wire` here — nothing in this stage touches the network, and the field means exactly that.
  record({ stage: 'inflate', ms: performance.now() - t0, t: performance.now(), bytes: out.byteLength });
  return out;
};
