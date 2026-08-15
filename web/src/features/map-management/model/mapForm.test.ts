import { describe, expect, it } from 'vitest';
import { draftFromForm, emptyMapForm, hoursToInterval, intervalFromParts, intervalHours, intervalParts } from './mapForm';

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

  it('round-trips schedule intervals through the API TimeSpan format', () => {
    expect(hoursToInterval('48')).toBe('2.00:00:00');
    expect(intervalHours('2.00:00:00')).toBe(48);
    expect(intervalFromParts({ intervalDays: '0', intervalHours: '1', intervalMinutes: '2', intervalSeconds: '3' })).toBe('01:02:03');
    expect(intervalFromParts({ intervalDays: '0', intervalHours: '0', intervalMinutes: '0', intervalSeconds: '1' })).toBe('00:00:01');
    expect(intervalParts('3.04:05:06')).toEqual({ days: 3, hours: 4, minutes: 5, seconds: 6 });
  });
});
