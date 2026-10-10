import { describe, expect, test } from 'vitest';
import type { ViewConfig } from '@/types/viewConfig';
import { buildFoldContext } from './foldContext';

describe('buildFoldContext', () => {
  test('maps an explicit numeric range to the same ordered context representation as dates', () => {
    const view: ViewConfig = {
      id: 'g', viewport: 'geo', mark: 'polygon',
      source: {
        adapter: 'test', query: '', geometry: { kind: 'quadkey' },
        channels: [{ name: 'tests', column: 'tests', role: 'measure', type: 'f32' }],
      },
      filters: [{ channel: 'tests', control: 'range' }],
    };
    expect(buildFoldContext(view, { tests: '5..10' })).toEqual({ equals: {}, ranges: { tests: { from: '5', to: '10' } } });
  });
});
