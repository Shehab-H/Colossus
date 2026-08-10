import { describe, expect, it } from 'vitest';
import { canonicalViewerUrl, resolveViewerRoute, urlWithViewerState } from './viewRoute';

describe('viewer routes', () => {
  it('resolves full and embedded map paths', () => {
    expect(resolveViewerRoute('https://maps.example/maps/mobile-coverage?z=4.8')).toEqual({
      mapId: 'mobile-coverage',
      surface: 'map',
      legacy: false,
    });
    expect(resolveViewerRoute('https://maps.example/embed/geonames')).toEqual({
      mapId: 'geonames',
      surface: 'embed',
      legacy: false,
    });
  });

  it('turns legacy query routes into canonical paths while preserving viewer state', () => {
    const legacy = resolveViewerRoute('https://maps.example/?embed=1&view=ookla-fixed&theme=light&z=4')!;
    const next = new URL(canonicalViewerUrl('https://maps.example/?embed=1&view=ookla-fixed&theme=light&z=4', legacy));
    expect(next.pathname).toBe('/embed/ookla-fixed');
    expect(next.searchParams.has('view')).toBe(false);
    expect(next.searchParams.has('embed')).toBe(false);
    expect(next.searchParams.get('theme')).toBe('light');
    expect(next.searchParams.get('z')).toBe('4');
  });

  it('serializes deterministic live state and keeps unrelated debug parameters', () => {
    const next = new URL(
      urlWithViewerState('https://maps.example/maps/x?perf=1&f_old=gone', {
        color: 'speed',
        theme: 'dark',
        camera: { longitude: 10, latitude: 50, zoom: 4.8 },
        filters: { operator: 'Vodafone', band: '(all)' },
      }),
    );
    expect(next.searchParams.get('perf')).toBe('1');
    expect(next.searchParams.get('color')).toBe('speed');
    expect(next.searchParams.get('lng')).toBe('10.00000');
    expect(next.searchParams.get('f_operator')).toBe('Vodafone');
    expect(next.searchParams.has('f_old')).toBe(false);
    expect(next.searchParams.has('f_band')).toBe(false);
  });
});
