import { afterEach, describe, expect, it, vi } from 'vitest';
import { gzipSync } from 'node:zlib';
import { Table, makeVector, tableToIPC } from 'apache-arrow';
import { fetchArrowBlock, inflateBlock } from './arrow';
import { packBlock } from './tileData';
import type { CompanionPack } from './manifest';

// A tiny companion-shaped Arrow IPC payload, gzipped the way CompanionPackWriter writes a block.
const arrowBytes = (mki: number[]): Uint8Array =>
  tableToIPC(new Table({ mki: makeVector(new Int32Array(mki)) } as never));

// Two blocks concatenated exactly like the bake's facts.pack: independent gzip members, back to back.
const pack = () => {
  const a = gzipSync(arrowBytes([0, 1, 2]));
  const b = gzipSync(arrowBytes([7]));
  const archive = new Uint8Array(a.length + b.length);
  archive.set(a, 0);
  archive.set(b, a.length);
  return { archive, entries: { '3/1/1': [0, a.length], '3/2/1': [a.length, b.length] } as Record<string, [number, number]> };
};

const slice = (u8: Uint8Array, off: number, len: number): ArrayBuffer =>
  u8.slice(off, off + len).buffer as ArrayBuffer;

describe('inflateBlock', () => {
  it('decompresses an exact-length body (a 206, or the service worker cache)', async () => {
    const { archive, entries } = pack();
    const [off, len] = entries['3/2/1'];
    const out = new Uint8Array(await inflateBlock(slice(archive, off, len), off, len, 'gzip'));
    expect(out).toEqual(arrowBytes([7]));
  });

  it('slices its block out of a whole-archive body (a server that ignored Range)', async () => {
    const { archive, entries } = pack();
    const [off, len] = entries['3/2/1'];
    const out = new Uint8Array(await inflateBlock(archive.slice().buffer as ArrayBuffer, off, len, 'gzip'));
    expect(out).toEqual(arrowBytes([7]));
  });
});

describe('fetchArrowBlock', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('ranges the block and parses the Arrow inside', async () => {
    const { archive, entries } = pack();
    const [off, len] = entries['3/1/1'];
    const fetchMock = vi.fn(async (_url: unknown, init?: RequestInit) => {
      const m = /^bytes=(\d+)-(\d+)$/.exec(new Headers(init?.headers).get('Range') ?? '');
      if (!m) throw new Error('expected a Range header');
      return new Response(slice(archive, Number(m[1]), Number(m[2]) - Number(m[1]) + 1), { status: 206 });
    });
    vi.stubGlobal('fetch', fetchMock);

    const { table } = await fetchArrowBlock('http://x/facts.pack?tile=3/1/1', off, len, 'gzip');
    expect(fetchMock).toHaveBeenCalledOnce();
    expect([...table.getChild('mki')!]).toEqual([0, 1, 2]);
  });

  it('keeps the per-file error shape on a missing archive (fold marks the tile, no retry loop)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));
    await expect(fetchArrowBlock('http://x/facts.pack?tile=3/1/1', 0, 10, 'gzip')).rejects.toThrow(/^tile 404 /);
  });
});

describe('packBlock', () => {
  const manifestPack: CompanionPack = { file: 'facts.pack', codec: 'gzip', entries: { '3/1/1': [0, 42] } };

  it('resolves a packed leaf to its ranged block', () => {
    const b = packBlock(manifestPack, 'view', 'v1', '3/1/1');
    expect(b).toEqual({ url: expect.stringContaining('/view/v1/facts.pack?tile=3/1/1'), offset: 0, length: 42, codec: 'gzip' });
  });

  it('is null for unpacked tiles (internal levels, older bakes) — the per-file fetch stays', () => {
    expect(packBlock(manifestPack, 'view', 'v1', '2/0/0')).toBeNull();
    expect(packBlock(undefined, 'view', 'v1', '3/1/1')).toBeNull();
  });
});
