import { describe, expect, it } from 'vitest';
import { applySourceSchema, emptyConnectionForm, emptyDatasetForm, parseConnectionForm, parseDatasetForm, readChannelSpecs } from './sourceForms';

describe('source management forms', () => {
  it('separates adapter-declared settings from secrets', () => {
    const form = { ...emptyConnectionForm('clickhouse'), slug: 'warehouse', name: 'Warehouse', values: { url: 'http://db', password: 'secret' } };
    const parsed = parseConnectionForm(form, {
      id: 'clickhouse',
      name: 'ClickHouse',
      capabilities: { connectionValidation: true, locationDiscovery: true, schemaInspection: true, boundedPreview: true, canonicalExtraction: true },
      configurationFields: [
        { key: 'url', label: 'URL', required: true, secret: false },
        { key: 'password', label: 'Password', required: false, secret: true },
      ],
      geometryKinds: ['lonLat'],
      datasetInputLabel: 'Source query',
      datasetInputDescription: 'Enter a query.',
      datasetInputPlaceholder: 'SELECT 1',
    });
    expect(parsed.errors).toEqual({});
    expect(parsed.value?.definition.settings).toEqual({ url: 'http://db' });
    expect(parsed.value?.secrets).toEqual({ password: 'secret' });
  });

  it('validates adapter-declared connection choices', () => {
    const adapter = {
      id: 'parquet',
      name: 'Raw Parquet',
      capabilities: { connectionValidation: true, locationDiscovery: true, schemaInspection: true, boundedPreview: true, canonicalExtraction: true },
      configurationFields: [
        { key: 'provider', label: 'Provider', required: true, secret: false, allowedValues: ['local', 'public'] },
      ],
      geometryKinds: ['lonLat'],
      datasetInputLabel: 'Parquet location',
      datasetInputDescription: 'Enter a provider-relative location.',
      datasetInputPlaceholder: 'places.parquet',
    };
    const invalid = parseConnectionForm(
      { ...emptyConnectionForm('parquet'), slug: 'files', name: 'Files', values: { provider: 'unknown' } },
      adapter,
    );
    expect(invalid.errors['connection.provider']?.[0]).toContain('local, public');
  });

  it('builds a canonical dataset mapping and reports malformed channels', () => {
    const valid = parseDatasetForm({
      ...emptyDatasetForm('connection'),
      slug: 'coverage',
      name: 'Coverage',
      query: 'SELECT lon, lat, speed FROM coverage',
      channels: '[{"name":"speed","column":"speed","role":"measure","type":"f64"}]',
    });
    expect(valid.value?.source.geometry).toEqual({ kind: 'lonLat', lon: 'lon', lat: 'lat' });
    expect(valid.value?.source.channels[0].name).toBe('speed');

    const invalid = parseDatasetForm({ ...emptyDatasetForm('connection'), slug: 'coverage', name: 'Coverage', query: 'SELECT 1', channels: '{' });
    expect(invalid.errors.channels).toBeDefined();
  });

  it('rejects incomplete, unknown, and duplicate channel declarations', () => {
    const base = { ...emptyDatasetForm('connection'), slug: 'coverage', name: 'Coverage', query: 'SELECT 1' };

    expect(parseDatasetForm({ ...base, channels: '[{"name":"speed"}]' }).errors.channels?.[0])
      .toContain('non-empty column');
    expect(parseDatasetForm({ ...base, channels: '[{"name":"speed","column":"speed","role":"metric","type":"f64"}]' }).errors.channels?.[0])
      .toContain('role must be one of');
    expect(parseDatasetForm({ ...base, channels: '[{"name":"speed","column":"a","role":"measure","type":"f64"},{"name":"speed","column":"b","role":"measure","type":"f32"}]' }).errors.channels?.[0])
      .toContain('must be unique');
  });

  it('derives selectable geometry and channel suggestions from inspected query columns', () => {
    const form = applySourceSchema(emptyDatasetForm('connection'), {
      columns: [
        { name: 'quadkey', sourceType: 'String', nullable: false },
        { name: 'quarter', sourceType: 'Date', nullable: false },
        { name: 'operator', sourceType: 'LowCardinality(String)', nullable: false },
        { name: 'tests', sourceType: 'UInt32', nullable: false },
      ],
    });

    expect(form.geometryKind).toBe('quadkey');
    expect(form.geometryColumn).toBe('quadkey');
    expect(readChannelSpecs(form.channels)).toEqual([
      { name: 'quarter', column: 'quarter', role: 'temporal', type: 'date' },
      { name: 'operator', column: 'operator', role: 'dimension', type: 'dict' },
      { name: 'tests', column: 'tests', role: 'measure', type: 'i64' },
    ]);
  });
});
