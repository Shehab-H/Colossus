import { afterEach, describe, expect, it, vi } from 'vitest';
import { gzipSync } from 'node:zlib';
import { Table, makeVector, tableToIPC } from 'apache-arrow';
import { fetchArrowBlock } from './arrowFetch';

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

