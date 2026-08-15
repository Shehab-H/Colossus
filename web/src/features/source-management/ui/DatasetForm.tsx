import type { FormEvent } from 'react';
import type { CatalogConnection } from '@entities/catalog';
import { Button, Field, Input, Select, Textarea } from '@shared/ui';
import type { DatasetFormState } from '../model/sourceForms';

export function DatasetForm({
  form,
  connections,
  geometryKinds,
  sourceInput,
  errors,
  busy,
  onChange,
  onSubmit,
}: {
  form: DatasetFormState;
  connections: CatalogConnection[];
  geometryKinds: string[];
  sourceInput: { label: string; description: string; placeholder: string };
  errors: Record<string, string[]>;
  busy: boolean;
  onChange: (next: DatasetFormState) => void;
  onSubmit: (event: FormEvent) => void;
}) {
  const pointGeometry = form.geometryKind === 'lonLat' || form.geometryKind === 'xy';
  return (
    <form className="source-form" onSubmit={onSubmit}>
      <div className="source-form__fields source-form__fields--three">
        <Field label="Dataset slug" error={errors.slug?.join(' ')}>
          <Input value={form.slug} onChange={(event) => onChange({ ...form, slug: event.target.value })} placeholder="mobile-coverage" />
        </Field>
        <Field label="Display name" error={errors.name?.join(' ')}>
          <Input value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="Mobile coverage" />
        </Field>
        <Field label="Connection" error={errors.connectionId?.join(' ')}>
          <Select value={form.connectionId} onChange={(event) => onChange({ ...form, connectionId: event.target.value })}>
            <option value="">Select connection</option>
            {connections.map((connection) => <option key={connection.id} value={connection.id}>{connection.name}</option>)}
          </Select>
        </Field>
      </div>
      <Field label={sourceInput.label} description={sourceInput.description} error={errors.query?.join(' ')}>
        <Textarea className="source-form__query" value={form.query} onChange={(event) => onChange({ ...form, query: event.target.value })} placeholder={sourceInput.placeholder} spellCheck={false} />
      </Field>
      <div className="source-form__fields source-form__fields--three">
        <Field label="Geometry mapping" error={errors.geometry?.join(' ')}>
          <Select value={form.geometryKind} onChange={(event) => onChange({ ...form, geometryKind: event.target.value as DatasetFormState['geometryKind'] })}>
            {geometryKinds.map((kind) => <option key={kind} value={kind}>{kind}</option>)}
          </Select>
        </Field>
        {pointGeometry ? (
          <>
            <Field label={form.geometryKind === 'lonLat' ? 'Longitude column' : 'X column'}>
              <Input value={form.firstCoordinate} onChange={(event) => onChange({ ...form, firstCoordinate: event.target.value })} />
            </Field>
            <Field label={form.geometryKind === 'lonLat' ? 'Latitude column' : 'Y column'}>
              <Input value={form.secondCoordinate} onChange={(event) => onChange({ ...form, secondCoordinate: event.target.value })} />
            </Field>
          </>
        ) : (
          <Field label={`${form.geometryKind} column`}>
            <Input value={form.geometryColumn} onChange={(event) => onChange({ ...form, geometryColumn: event.target.value })} />
          </Field>
        )}
      </div>
      <Field label="Typed channels" description="Canonical channel declarations: name, column, role, and type." error={errors.channels?.join(' ')}>
        <Textarea className="source-form__channels" value={form.channels} onChange={(event) => onChange({ ...form, channels: event.target.value })} spellCheck={false} />
      </Field>
      <div className="source-form__actions">
        <Button variant="primary" type="submit" disabled={busy || connections.length === 0}>{busy ? 'Saving…' : 'Save dataset'}</Button>
      </div>
    </form>
  );
}
