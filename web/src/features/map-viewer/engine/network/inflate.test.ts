import { describe, expect, it } from 'vitest';
import { gzipSync } from 'node:zlib';
import { Table, makeVector, tableToIPC } from 'apache-arrow';
import { inflateBlock } from './inflate';

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

