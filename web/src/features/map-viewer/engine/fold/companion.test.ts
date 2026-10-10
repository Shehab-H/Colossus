import { describe, expect, it } from 'vitest';
import { Table, Utf8, makeBuilder, makeVector, tableFromIPC, tableToIPC } from 'apache-arrow';
import type { Manifest } from '@/types/manifest';
import { companionGrain, decodeCompanion } from './companion';

const utf8Vec = (vals: (string | null)[]) => {
  const b = makeBuilder({ type: new Utf8(), nullValues: [null] });
  for (const v of vals) b.append(v);
  return b.finish().toVector();
};

// IPC round-trip so decode sees exactly what fetchArrowTable produces (single-chunk stream data).
const roundTrip = (cols: Record<string, unknown>) => tableFromIPC(tableToIPC(new Table(cols as never)));

// Format-2 round-trip: like fetchArrowTable, the table is parsed from a standalone ArrayBuffer, so its
// column buffers are views into it and the same buffer is what decodeTile retains and views against.
describe('decodeCompanion', () => {
  const manifest = {
    version: 'v',
    view: {
      id: 'g',
      viewport: 'geo',
      mark: 'polygon',
      source: {
        adapter: 'test',
        query: '',
        geometry: { kind: 'quadkey' },
        channels: [
          { name: 'operator', column: 'operator', role: 'dimension', type: 'dict' },
          { name: 'quarter', column: 'quarter', role: 'temporal', type: 'date' },
          { name: 'tests', column: 'tests', role: 'measure', type: 'f32' },
        ],
      },
      measures: [{ name: 'total_tests', expr: 'sum(tests)' }],
    },
    grainChannels: ['operator', 'quarter', 'tests'],
  } as unknown as Manifest;

  it('splits mki, grain dims (codes), grain temporal (day numbers), and partial columns', () => {
    const table = roundTrip({
      mki: makeVector(new Int32Array([0, 0, 1])),
      operator: utf8Vec(['apex', 'zenith', 'apex']),
      quarter: utf8Vec(['2025-01-01', '2025-01-01', '2025-04-01']),
      tests: makeVector(new Float32Array([10, 3, 20])),
      sum__tests: makeVector(new Float32Array([10, 3, 20])),
    });
    const c = decodeCompanion(table, companionGrain(manifest));

    expect(c.rowCount).toBe(3);
    expect([...c.mki]).toEqual([0, 0, 1]);
    expect(c.dim.operator.dict).toEqual(['apex', 'zenith']);
    expect([...c.dim.operator.codes]).toEqual([0, 1, 0]);
    const day = (iso: string) => Math.floor(Date.parse(`${iso}T00:00:00Z`) / 86400000);
    expect([...c.temporalDays.quarter]).toEqual([day('2025-01-01'), day('2025-01-01'), day('2025-04-01')]);
    expect([...c.numericValues!.tests]).toEqual([10, 3, 20]);
    expect([...c.partial.sum__tests]).toEqual([10, 3, 20]);
  });

  it('throws on a companion without mki (an old bake) so the fold is skipped, not wrong', () => {
    const table = roundTrip({
      mk: utf8Vec(['p:1']),
      operator: utf8Vec(['apex']),
      quarter: utf8Vec(['2025-01-01']),
      sum__tests: makeVector(new Float32Array([10])),
    });
    expect(() => decodeCompanion(table, companionGrain(manifest))).toThrow('mki');
  });
});
