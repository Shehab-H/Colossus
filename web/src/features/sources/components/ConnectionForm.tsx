import type { FormEvent } from 'react';
import { Button, Field, Input, Select } from '@/components/ui';
import type { ConnectionFormState } from '../forms/sourceForms';
import type { SourceAdapterDescriptor } from '@/types/source';

export function ConnectionForm({
  form,
  adapters,
  errors,
  busy,
  onChange,
  onSubmit,
}: {
  form: ConnectionFormState;
  adapters: SourceAdapterDescriptor[];
  errors: Record<string, string[]>;
  busy: boolean;
  onChange: (next: ConnectionFormState) => void;
  onSubmit: (event: FormEvent) => void;
}) {
  const adapter = adapters.find((item) => item.id === form.adapter);
  return (
    <form className="source-form" onSubmit={onSubmit}>
      <div className="source-form__fields source-form__fields--three">
        <Field label="Connection slug" error={errors.slug?.join(' ')}>
          <Input value={form.slug} onChange={(event) => onChange({ ...form, slug: event.target.value })} placeholder="primary-warehouse" />
        </Field>
        <Field label="Display name" error={errors.name?.join(' ')}>
          <Input value={form.name} onChange={(event) => onChange({ ...form, name: event.target.value })} placeholder="Primary warehouse" />
        </Field>
        <Field label="Adapter" error={errors.adapter?.join(' ')}>
          <Select value={form.adapter} onChange={(event) => onChange({ ...form, adapter: event.target.value, values: {} })}>
            <option value="">Select adapter</option>
            {adapters.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
          </Select>
        </Field>
      </div>
      {adapter && (
        <div className="source-form__fields source-form__fields--three">
          {adapter.configurationFields.map((field) => (
            <Field key={field.key} label={field.label} error={errors[`connection.${field.key}`]?.join(' ')}>
              {field.allowedValues ? (
                <Select
                  value={form.values[field.key] ?? ''}
                  onChange={(event) => onChange({
                    ...form,
                    values: { ...form.values, [field.key]: event.target.value },
                  })}
                >
                  <option value="">Select {field.label.toLowerCase()}</option>
                  {field.allowedValues.map((value) => <option key={value} value={value}>{value}</option>)}
                </Select>
              ) : (
                <Input
                  type={field.secret ? 'password' : 'text'}
                  value={form.values[field.key] ?? ''}
                  placeholder={field.placeholder}
                  autoComplete={field.secret ? 'new-password' : 'off'}
                  onChange={(event) => onChange({
                    ...form,
                    values: { ...form.values, [field.key]: event.target.value },
                  })}
                />
              )}
            </Field>
          ))}
        </div>
      )}
      <div className="source-form__actions">
        <Button variant="primary" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Save connection'}</Button>
      </div>
    </form>
  );
}
