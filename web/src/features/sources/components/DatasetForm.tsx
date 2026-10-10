import type { FormEvent } from 'react';
import { Badge, Button, Field, Input, Select, Textarea } from '@/components/ui';
import { readChannelSpecs, suggestedChannel, type DatasetFormState } from '../forms/sourceForms';
import type { CatalogConnection, SourceSchema, DatasetChannelSpec } from '@/types/source';

const roles = ['measure', 'dimension', 'temporal', 'identity'];
const types = ['f32', 'f64', 'u8', 'u16', 'i32', 'i64', 'dict', 'date'];

export function DatasetForm({
  form,
  connections,
  geometryKinds,
  sourceInput,
  schema,
  errors,
  busy,
  inspecting,
  onChange,
  onInspect,
  onSubmit,
}: {
  form: DatasetFormState;
  connections: CatalogConnection[];
  geometryKinds: string[];
  sourceInput: { label: string; description: string; placeholder: string };
  schema?: SourceSchema;
  errors: Record<string, string[]>;
  busy: boolean;
  inspecting: boolean;
  onChange: (next: DatasetFormState) => void;
  onInspect: () => void;
  onSubmit: (event: FormEvent) => void;
}) {
  const pointGeometry = form.geometryKind === 'lonLat' || form.geometryKind === 'xy';
  const channels = readChannelSpecs(form.channels);
  const geometryColumns = new Set(pointGeometry
    ? [form.firstCoordinate, form.secondCoordinate]
    : [form.geometryColumn]);
  const setChannels = (next: DatasetChannelSpec[]) => onChange({ ...form, channels: JSON.stringify(next, null, 2) });
  const updateChannel = (column: string, patch: Partial<DatasetChannelSpec>) => {
    const existing = channels.find((channel) => channel.column === column);
    if (!existing) return;
    setChannels(channels.map((channel) => channel.column === column ? { ...channel, ...patch } : channel));
  };
  const toggleChannel = (column: SourceSchema['columns'][number], enabled: boolean) => {
    if (!enabled) {
      setChannels(channels.filter((channel) => channel.column !== column.name));
      return;
    }
    const suggestion = suggestedChannel(column);
    if (suggestion) setChannels([...channels, suggestion]);
  };

  return (
    <form className="source-form" onSubmit={onSubmit}>
      <div className="source-form__fields source-form__fields--three">
        <Field label="Dataset slug" error={errors.slug?.join(' ')}>
          <Input value={form.slug} onChange={(event) => onChange({ ...form, slug: event.target.value })} placeholder="mobile-coverage" />
        </Field>
        <Field label="Display name" error={errors.name?.join(' ')}>
          <Input value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="Mobile coverage" />
        </Field>
        <Field label="Connection" description="Datasets use a saved connection; credentials are not duplicated." error={errors.connectionId?.join(' ')}>
          <Select value={form.connectionId} onChange={(event) => onChange({ ...form, connectionId: event.target.value })}>
            <option value="">Select connection</option>
            {connections.map((connection) => <option key={connection.id} value={connection.id}>{connection.name}</option>)}
          </Select>
        </Field>
      </div>

      <div className="source-form__query-step">
        <Field label={sourceInput.label} description={sourceInput.description} error={errors.query?.join(' ')}>
          <Textarea className="source-form__query" value={form.query} onChange={(event) => onChange({ ...form, query: event.target.value })} placeholder={sourceInput.placeholder} spellCheck={false} />
        </Field>
        <div className="source-form__inspect-action">
          <Button variant="secondary" onClick={onInspect} disabled={inspecting || !form.connectionId || !form.query.trim()}>
            {inspecting ? 'Inspecting…' : schema ? 'Refresh query columns' : 'Inspect query columns'}
          </Button>
          <span>Runs a zero-row schema query. It does not save the dataset or launch a bake.</span>
        </div>
      </div>

      {!schema ? (
        <div className="source-form__schema-empty">
          <strong>Geometry and channels are configured after query inspection.</strong>
          <span>Choose a connection, enter the final query, then inspect its result columns.</span>
        </div>
      ) : (
        <div className="source-form__mapping">
          <div className="source-form__schema-heading">
            <div><strong>{schema.columns.length} result columns</strong><span>Select the geometry mapping, then keep only columns the map needs.</span></div>
            <Badge tone="success">Schema inspected</Badge>
          </div>
          {schema.warnings?.map((warning) => <Badge key={warning} tone="warning">{warning}</Badge>)}

          <div className="source-form__fields source-form__fields--three">
            <Field label="Geometry format" error={errors.geometry?.join(' ')}>
              <Select value={form.geometryKind} onChange={(event) => onChange({ ...form, geometryKind: event.target.value as DatasetFormState['geometryKind'] })}>
                {geometryKinds.map((kind) => <option key={kind} value={kind}>{geometryLabel(kind)}</option>)}
              </Select>
            </Field>
            {pointGeometry ? (
              <>
                <ColumnSelect label={form.geometryKind === 'lonLat' ? 'Longitude column' : 'X column'} value={form.firstCoordinate} schema={schema} onChange={(firstCoordinate) => onChange({ ...form, firstCoordinate })} />
                <ColumnSelect label={form.geometryKind === 'lonLat' ? 'Latitude column' : 'Y column'} value={form.secondCoordinate} schema={schema} onChange={(secondCoordinate) => onChange({ ...form, secondCoordinate })} />
              </>
            ) : (
              <ColumnSelect label={`${geometryLabel(form.geometryKind)} column`} value={form.geometryColumn} schema={schema} onChange={(geometryColumn) => onChange({ ...form, geometryColumn })} />
            )}
          </div>

          <div className="source-form__channel-table-wrap">
            <table className="source-form__channel-table">
              <thead><tr><th>Use</th><th>Query column</th><th>Map channel</th><th>Role</th><th>Type</th></tr></thead>
              <tbody>{schema.columns.map((column) => {
                const channel = channels.find((item) => item.column === column.name);
                const geometry = geometryColumns.has(column.name);
                const supported = Boolean(suggestedChannel(column));
                return <tr key={column.name}>
                  <td><input type="checkbox" aria-label={`Use ${column.name} as a channel`} checked={Boolean(channel)} disabled={geometry || !supported} onChange={(event) => toggleChannel(column, event.target.checked)} /></td>
                  <td><strong>{column.name}</strong><small>{column.sourceType}{column.nullable ? ' · nullable' : ''}{geometry ? ' · geometry' : ''}</small></td>
                  <td><Input aria-label={`${column.name} channel name`} value={channel?.name ?? ''} disabled={!channel} onChange={(event) => updateChannel(column.name, { name: event.target.value })} /></td>
                  <td><Select aria-label={`${column.name} channel role`} value={channel?.role ?? ''} disabled={!channel} onChange={(event) => updateChannel(column.name, { role: event.target.value })}>{roles.map((role) => <option key={role} value={role}>{role}</option>)}</Select></td>
                  <td><Select aria-label={`${column.name} channel type`} value={channel?.type ?? ''} disabled={!channel} onChange={(event) => updateChannel(column.name, { type: event.target.value })}>{types.map((type) => <option key={type} value={type}>{type}</option>)}</Select></td>
                </tr>;
              })}</tbody>
            </table>
          </div>
          {errors.channels && <span className="source-form__mapping-error">{errors.channels.join(' ')}</span>}
          <details className="source-form__advanced">
            <summary>Advanced channel JSON</summary>
            <p>The guided table writes this contract. Edit it directly only when the adapter needs an unusual mapping.</p>
            <Textarea className="source-form__channels" value={form.channels} onChange={(event) => onChange({ ...form, channels: event.target.value })} spellCheck={false} />
          </details>
        </div>
      )}

      <div className="source-form__actions">
        <Button variant="primary" type="submit" disabled={busy || connections.length === 0 || !schema}>{busy ? 'Saving…' : 'Save dataset'}</Button>
      </div>
    </form>
  );
}

function ColumnSelect({ label, value, schema, onChange }: { label: string; value: string; schema: SourceSchema; onChange: (value: string) => void }) {
  return <Field label={label}><Select value={value} onChange={(event) => onChange(event.target.value)}><option value="">Select column</option>{schema.columns.map((column) => <option key={column.name} value={column.name}>{column.name} · {column.sourceType}</option>)}</Select></Field>;
}

function geometryLabel(kind: string): string {
  if (kind === 'lonLat') return 'Longitude / latitude';
  if (kind === 'xy') return 'X / Y coordinates';
  if (kind === 'quadkey') return 'Quadkey';
  if (kind === 'wkt') return 'WKT geometry';
  return kind;
}
