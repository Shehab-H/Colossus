import type {
  ChannelSpec,
  CreateConnectionInput,
  CreateDatasetInput,
  GeometrySpec,
  SourceAdapterDescriptor,
  SourceSchema,
} from '@entities/catalog';

export interface ConnectionFormState {
  slug: string;
  name: string;
  adapter: string;
  values: Record<string, string>;
}

export interface DatasetFormState {
  slug: string;
  name: string;
  connectionId: string;
  query: string;
  geometryKind: 'lonLat' | 'xy' | 'quadkey' | 'wkt';
  firstCoordinate: string;
  secondCoordinate: string;
  geometryColumn: string;
  channels: string;
}

export interface ParsedForm<T> {
  value?: T;
  errors: Record<string, string[]>;
}

const channelRoles = new Set(['measure', 'dimension', 'temporal', 'identity']);
const channelTypes = new Set(['f32', 'f64', 'u8', 'u16', 'i32', 'i64', 'dict', 'date']);

export const emptyConnectionForm = (adapter = ''): ConnectionFormState => ({
  slug: '',
  name: '',
  adapter,
  values: {},
});

export const emptyDatasetForm = (connectionId = ''): DatasetFormState => ({
  slug: '',
  name: '',
  connectionId,
  query: '',
  geometryKind: 'lonLat',
  firstCoordinate: 'lon',
  secondCoordinate: 'lat',
  geometryColumn: '',
  channels: '[]',
});

export function applySourceSchema(form: DatasetFormState, schema: SourceSchema): DatasetFormState {
  const byName = new Map(schema.columns.map((column) => [column.name.toLowerCase(), column.name]));
  const find = (...names: string[]) => names.map((name) => byName.get(name)).find(Boolean);
  const quadkey = find('quadkey', 'quad_key');
  const wkt = find('wkt', 'geometry_wkt', 'geom_wkt');
  const lon = find('lon', 'lng', 'longitude');
  const lat = find('lat', 'latitude');
  const x = find('x');
  const y = find('y');

  let next: DatasetFormState = { ...form };
  const geometryColumns = new Set<string>();
  if (quadkey) {
    next = { ...next, geometryKind: 'quadkey', geometryColumn: quadkey };
    geometryColumns.add(quadkey);
  } else if (wkt) {
    next = { ...next, geometryKind: 'wkt', geometryColumn: wkt };
    geometryColumns.add(wkt);
  } else if (lon && lat) {
    next = { ...next, geometryKind: 'lonLat', firstCoordinate: lon, secondCoordinate: lat };
    geometryColumns.add(lon);
    geometryColumns.add(lat);
  } else if (x && y) {
    next = { ...next, geometryKind: 'xy', firstCoordinate: x, secondCoordinate: y };
    geometryColumns.add(x);
    geometryColumns.add(y);
  }

  const channels = schema.columns
    .filter((column) => !geometryColumns.has(column.name))
    .map(suggestedChannel)
    .filter((channel): channel is ChannelSpec => channel !== undefined);
  return { ...next, channels: JSON.stringify(channels, null, 2) };
}

export function readChannelSpecs(source: string): ChannelSpec[] {
  try {
    const parsed = JSON.parse(source || '[]');
    return Array.isArray(parsed) ? parsed.map(parseChannel) : [];
  } catch {
    return [];
  }
}

export function suggestedChannel(column: SourceSchema['columns'][number]): ChannelSpec | undefined {
  const sourceType = column.sourceType.toLowerCase();
  const name = column.name;
  const temporal = /date|time/.test(sourceType) || /(^|_)(date|time|timestamp|quarter|year|month)($|_)/i.test(name);
  const identity = /(^id$|_id$|^uuid$)/i.test(name);
  const stringLike = /string|varchar|char|enum|uuid/.test(sourceType);
  const numeric = /float|double|decimal|int|number|real/.test(sourceType);
  if (temporal) return { name, column: name, role: 'temporal', type: 'date' };
  if (stringLike) return { name, column: name, role: identity ? 'identity' : 'dimension', type: 'dict' };
  if (!numeric) return;
  const type = /float32|\breal\b/.test(sourceType) ? 'f32'
    : /float64|double|decimal/.test(sourceType) ? 'f64'
      : /uint8/.test(sourceType) ? 'u8'
        : /uint16/.test(sourceType) ? 'u16'
          : /uint32|uint64|int64/.test(sourceType) ? 'i64'
            : /int32/.test(sourceType) ? 'i32'
              : 'f64';
  return { name, column: name, role: identity ? 'identity' : 'measure', type };
}

export function parseConnectionForm(
  form: ConnectionFormState,
  adapter: SourceAdapterDescriptor | undefined,
): ParsedForm<CreateConnectionInput> {
  const errors: Record<string, string[]> = {};
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(form.slug)) errors.slug = ['Use lowercase kebab-case.'];
  if (!form.name.trim()) errors.name = ['Enter a connection name.'];
  if (!adapter) errors.adapter = ['Select an adapter.'];
  const settings: Record<string, string> = {};
  const secrets: Record<string, string> = {};
  for (const field of adapter?.configurationFields ?? []) {
    const value = form.values[field.key]?.trim() ?? '';
    if (field.required && !value) errors[`connection.${field.key}`] = [`Enter ${field.label.toLowerCase()}.`];
    if (value && field.allowedValues && !field.allowedValues.includes(value)) {
      errors[`connection.${field.key}`] = [`Choose one of: ${field.allowedValues.join(', ')}.`];
    }
    if (value) (field.secret ? secrets : settings)[field.key] = value;
  }
  return Object.keys(errors).length > 0 ? { errors } : {
    errors,
    value: {
      definition: { slug: form.slug, name: form.name.trim(), adapter: form.adapter, settings },
      secrets,
    },
  };
}

export function parseDatasetForm(form: DatasetFormState): ParsedForm<CreateDatasetInput> {
  const errors: Record<string, string[]> = {};
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(form.slug)) errors.slug = ['Use lowercase kebab-case.'];
  if (!form.name.trim()) errors.name = ['Enter a dataset name.'];
  if (!form.connectionId) errors.connectionId = ['Select a connection.'];
  if (!form.query.trim()) errors.query = ['Enter a bounded source query or table projection.'];
  let channels: ChannelSpec[] = [];
  try {
    const parsed = JSON.parse(form.channels || '[]');
    if (!Array.isArray(parsed)) throw new Error('Channels must be a JSON array.');
    channels = parsed.map(parseChannel);
    const duplicates = channels
      .map((channel) => channel.name)
      .filter((name, index, names) => names.indexOf(name) !== index);
    if (duplicates.length > 0) throw new Error(`Channel names must be unique: ${[...new Set(duplicates)].join(', ')}.`);
  } catch (error) {
    errors.channels = [error instanceof Error ? error.message : String(error)];
  }
  const geometry = geometryFromForm(form, errors);
  return Object.keys(errors).length > 0 || !geometry ? { errors } : {
    errors,
    value: {
      slug: form.slug,
      name: form.name.trim(),
      connectionId: form.connectionId,
      source: { query: form.query.trim(), geometry, channels },
    },
  };
}

function parseChannel(value: unknown, index: number): ChannelSpec {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`Channel ${index + 1} must be an object.`);
  }
  const candidate = value as Record<string, unknown>;
  const name = requiredChannelString(candidate, 'name', index);
  const column = requiredChannelString(candidate, 'column', index);
  const role = requiredChannelString(candidate, 'role', index);
  const type = requiredChannelString(candidate, 'type', index);
  if (!channelRoles.has(role)) {
    throw new Error(`Channel ${index + 1} role must be one of: ${[...channelRoles].join(', ')}.`);
  }
  if (!channelTypes.has(type)) {
    throw new Error(`Channel ${index + 1} type must be one of: ${[...channelTypes].join(', ')}.`);
  }
  return { name, column, role, type };
}

function requiredChannelString(value: Record<string, unknown>, field: string, index: number): string {
  const candidate = value[field];
  if (typeof candidate !== 'string' || !candidate.trim()) {
    throw new Error(`Channel ${index + 1} needs a non-empty ${field}.`);
  }
  return candidate.trim();
}

function geometryFromForm(form: DatasetFormState, errors: Record<string, string[]>): GeometrySpec | undefined {
  if (form.geometryKind === 'lonLat') {
    if (!form.firstCoordinate.trim() || !form.secondCoordinate.trim()) {
      errors.geometry = ['Enter longitude and latitude columns.'];
      return;
    }
    return { kind: 'lonLat', lon: form.firstCoordinate.trim(), lat: form.secondCoordinate.trim() };
  }
  if (form.geometryKind === 'xy') {
    if (!form.firstCoordinate.trim() || !form.secondCoordinate.trim()) {
      errors.geometry = ['Enter x and y columns.'];
      return;
    }
    return { kind: 'xy', x: form.firstCoordinate.trim(), y: form.secondCoordinate.trim(), geographic: false };
  }
  if (!form.geometryColumn.trim()) {
    errors.geometry = [`Enter the ${form.geometryKind} column.`];
    return;
  }
  return { kind: form.geometryKind, column: form.geometryColumn.trim(), geographic: true };
}
