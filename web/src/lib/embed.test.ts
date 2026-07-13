import { describe, expect, it } from 'vitest';
import { buildEmbedUrl, embedSnippet, readEmbedParams } from './embed';

describe('readEmbedParams', () => {
  it('parses the basic embed shape', () => {
    const p = readEmbedParams('?embed=1&view=ookla-fixed&color=download_mbps&theme=light&lng=10&lat=50&z=3.6');
    expect(p.embed).toBe(true);
    expect(p.view).toBe('ookla-fixed');
    expect(p.color).toBe('download_mbps');
    expect(p.theme).toBe('light');
    expect(p.camera).toEqual({ longitude: 10, latitude: 50, zoom: 3.6 });
  });

  it('is not embed and has no camera when params are absent', () => {
    const p = readEmbedParams('?view=x');
    expect(p.embed).toBe(false);
    expect(p.camera).toBeNull();
    expect(p.theme).toBeNull();
  });

  it('ignores a partial camera (needs lng, lat and z)', () => {
    expect(readEmbedParams('?lng=10&lat=50').camera).toBeNull();
  });

  it('collects f_ filters', () => {
    expect(readEmbedParams('?f_operator=Vodafone&f_band=5').filters).toEqual({ operator: 'Vodafone', band: '5' });
  });

  it('shows showcase controls by default and locks them with controls=0', () => {
    expect(readEmbedParams('?embed=1').controls).toBe(true);
    expect(readEmbedParams('?embed=1&controls=0').controls).toBe(false);
  });

  it('builds a color scale override from scale knobs', () => {
    const p = readEmbedParams('?color=v&scale=quantize&bins=6&reverse=1');
    expect(p.colorSpec).toEqual({ channel: 'v', type: 'quantize', bins: 6, reverse: true });
  });

  it('reads diverging midpoint and scheme', () => {
    expect(readEmbedParams('?color=lat&scale=diverging&midpoint=40&scheme=blueRed').colorSpec).toEqual({
      channel: 'lat',
      type: 'diverging',
      midpoint: 40,
      scheme: 'blueRed',
    });
  });

  it('has no colorSpec override when only the channel is given', () => {
    expect(readEmbedParams('?color=v').colorSpec).toBeNull();
  });

  it('rejects an unknown scale type', () => {
    expect(readEmbedParams('?color=v&scale=bogus').colorSpec).toBeNull();
  });
});

describe('buildEmbedUrl', () => {
  const base = 'https://host.example/app';

  it('round-trips through readEmbedParams', () => {
    const url = buildEmbedUrl(base, {
      view: 'ookla-fixed',
      color: 'download_mbps',
      theme: 'dark',
      camera: { longitude: -98, latitude: 39, zoom: 3.1 },
    });
    const p = readEmbedParams(new URL(url).search);
    expect(p.embed).toBe(true);
    expect(p.view).toBe('ookla-fixed');
    expect(p.color).toBe('download_mbps');
    expect(p.theme).toBe('dark');
    expect(p.camera).toEqual({ longitude: -98, latitude: 39, zoom: 3.1 });
  });

  it('drops "(all)" filters and keeps real ones', () => {
    const url = buildEmbedUrl(base, { view: 'v', filters: { keep: 'x', drop: '(all)' } });
    const q = new URL(url).searchParams;
    expect(q.get('f_keep')).toBe('x');
    expect(q.has('f_drop')).toBe(false);
  });
});

describe('embedSnippet', () => {
  it('wraps the url in an iframe with defaults', () => {
    const s = embedSnippet('https://host/app?embed=1');
    expect(s).toContain('src="https://host/app?embed=1"');
    expect(s).toContain('width="100%"');
    expect(s).toContain('height="480"');
    expect(s.startsWith('<iframe')).toBe(true);
  });
});
