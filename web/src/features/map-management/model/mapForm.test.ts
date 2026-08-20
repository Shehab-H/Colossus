import { describe, expect, it } from 'vitest';
import { draftFromForm, emptyMapForm, hoursToInterval, intervalFromParts, intervalHours, intervalParts, mapDraftsEqual } from './mapForm';

describe('map editor model', () => {
  it('ties JSON and identity failures to their fields', () => {
    const result = draftFromForm({
      ...emptyMapForm(),
      slug: 'Not valid',
      measures: '{broken',
    });
    expect(result.errors.slug).toBeDefined();
    expect(result.errors.datasetId).toBeDefined();
    expect(result.errors.measures).toBeDefined();
  });

  it('builds a typed draft without serializing empty optional objects', () => {
    const result = draftFromForm({ ...emptyMapForm(), slug: 'coverage', datasetId: 'dataset-id' });
    expect(result.errors).toEqual({});
    expect(result.draft).toMatchObject({ slug: 'coverage', datasetId: 'dataset-id', measures: [] });
    expect(result.draft?.filters).toBeUndefined();
    expect(result.draft?.encoding).toBeUndefined();
  });

  it('blocks invalid guided color values before a save reaches the API', () => {
    const result = draftFromForm({
      ...emptyMapForm(),
      slug: 'coverage',
      datasetId: 'dataset-id',
      encoding: JSON.stringify({ color: { channel: 'value', type: 'linear', range: ['#123456', '#12zzzz'] } }),
    });
    expect(result.draft).toBeUndefined();
    expect(result.errors.encoding).toEqual(['Colors must use #RGB or #RRGGBB hexadecimal notation.']);
  });

  it('round-trips schedule intervals through the API TimeSpan format', () => {
    expect(hoursToInterval('48')).toBe('2.00:00:00');
    expect(intervalHours('2.00:00:00')).toBe(48);
    expect(intervalFromParts({ intervalDays: '0', intervalHours: '1', intervalMinutes: '2', intervalSeconds: '3' })).toBe('01:02:03');
    expect(intervalFromParts({ intervalDays: '0', intervalHours: '0', intervalMinutes: '0', intervalSeconds: '1' })).toBe('00:00:01');
    expect(intervalParts('3.04:05:06')).toEqual({ days: 3, hours: 4, minutes: 5, seconds: 6 });
  });

  it('compares definitions independent of object key order while detecting real changes', () => {
    const base = draftFromForm({ ...emptyMapForm(), slug: 'coverage', datasetId: 'dataset-id' }).draft!;
    const reordered = { datasetId: base.datasetId, slug: base.slug, mark: base.mark, viewport: base.viewport, schemaVersion: 1, bakeFilters: [], measures: [] };
    expect(mapDraftsEqual(base, reordered)).toBe(true);
    expect(mapDraftsEqual(base, { ...base, title: 'Changed' })).toBe(false);
  });
});
