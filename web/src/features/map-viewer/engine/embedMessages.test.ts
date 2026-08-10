import { describe, expect, it } from 'vitest';
import { EMBED_MESSAGE_SOURCE, EMBED_MESSAGE_VERSION, embedEvent } from './embedMessages';

describe('embed event contract', () => {
  it('versions ready events', () => {
    expect(embedEvent({ type: 'ready', mapId: 'geonames', release: 'v1' })).toEqual({
      source: EMBED_MESSAGE_SOURCE,
      version: EMBED_MESSAGE_VERSION,
      type: 'ready',
      mapId: 'geonames',
      release: 'v1',
    });
  });

  it('allows a cleared selection without changing the envelope', () => {
    expect(embedEvent({ type: 'selection', mapId: 'geonames', selection: null })).toMatchObject({
      source: 'colossus.map-viewer',
      version: 1,
      selection: null,
    });
  });
});
