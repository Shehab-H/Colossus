import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { sourceAdapters } from '../../../../.storybook/fixtures/sources';
import { emptyConnectionForm, type ConnectionFormState } from '../forms/sourceForms';
import { ConnectionForm } from './ConnectionForm';

type FormProps = Parameters<typeof ConnectionForm>[0];

const meta = {
  title: 'Sources/ConnectionForm',
  component: ConnectionForm,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Creates a source connection. The fields after the slug, name and adapter come from the selected adapter descriptor (`GET /api/source-adapters`): a field with `allowedValues` renders as a select, a `secret` field as a password input.',
          'Changing the adapter clears the adapter-specific values.',
          '',
          'The form is controlled: `form` is a `ConnectionFormState`, and validation messages arrive through `errors`, keyed `slug`, `name`, `adapter` and `connection.<field key>`.',
        ].join('\n'),
      },
    },
  },
  args: {
    form: emptyConnectionForm(),
    adapters: sourceAdapters,
    errors: {},
    busy: false,
    onChange: fn(),
    onSubmit: fn((event) => event.preventDefault()),
  },
  argTypes: {
    form: { control: 'object', description: 'Current form values.' },
    adapters: { control: 'object', description: 'Adapter descriptors that define the available configuration fields.' },
    errors: { control: 'object', description: 'Validation messages keyed by field.' },
    busy: { control: 'boolean', description: 'Disables the submit button and shows "Saving…".' },
    onChange: { control: false },
    onSubmit: { control: false },
  },
} satisfies Meta<typeof ConnectionForm>;

export default meta;
type Story = StoryObj<typeof meta>;

function FormHost({ form: initial, onChange, ...props }: FormProps) {
  const [form, setForm] = useState<ConnectionFormState>(initial);
  return (
    <ConnectionForm
      {...props}
      form={form}
      onChange={(next) => {
        onChange(next);
        setForm(next);
      }}
    />
  );
}

const render: Story['render'] = (args) => <FormHost {...args} />;

export const Empty: Story = {
  render,
  play: async ({ canvas, userEvent }) => {
    await userEvent.selectOptions(canvas.getByRole('combobox', { name: /Adapter/ }), 'clickhouse');
    await expect(await canvas.findByLabelText(/Host/)).toBeInTheDocument();
    await expect(canvas.getByLabelText(/Password/)).toHaveAttribute('type', 'password');
  },
};

export const AdapterSelected: Story = {
  render,
  args: {
    form: {
      slug: 'primary-warehouse',
      name: 'Primary warehouse',
      adapter: 'clickhouse',
      values: { host: 'clickhouse.internal', port: '8123', protocol: 'https' },
    },
  },
  parameters: { docs: { description: { story: 'Adapter fields appear below the identity fields. `Protocol` has `allowedValues`, so it is a select; `Password` is a secret.' } } },
};

export const WithErrors: Story = {
  render,
  args: {
    form: { slug: 'Primary Warehouse', name: '', adapter: 'clickhouse', values: {} },
    errors: {
      slug: ['Use lowercase kebab-case.'],
      name: ['Enter a connection name.'],
      'connection.host': ['Enter host.'],
      'connection.protocol': ['Choose one of: http, https.'],
    },
  },
};

export const Saving: Story = {
  render,
  args: { busy: true, form: { slug: 'coverage-files', name: 'Coverage files', adapter: 'parquet', values: { root: '/data/parquet' } } },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Saving…' })).toBeDisabled();
  },
};
