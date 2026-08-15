import type {
  ChannelSpec,
  CreateConnectionInput,
  CreateDatasetInput,
  GeometrySpec,
  SourceAdapterDescriptor,
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
