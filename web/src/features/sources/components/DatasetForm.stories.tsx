import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { connections, coverageSchema, sourceAdapters } from '../../../../.storybook/fixtures/sources';
import { applySourceSchema, emptyDatasetForm, type DatasetFormState } from '../forms/sourceForms';
import { DatasetForm } from './DatasetForm';

type FormProps = Parameters<typeof DatasetForm>[0];

const adapter = sourceAdapters[0];
const sourceInput = {
  label: adapter.datasetInputLabel,
  description: adapter.datasetInputDescription,
  placeholder: adapter.datasetInputPlaceholder,
};
const query = 'SELECT lon, lat, signal_strength, tests, network, country_code, observed_on FROM coverage';
const draft: DatasetFormState = {
  ...emptyDatasetForm('connection-warehouse'),
  slug: 'mobile-coverage',
  name: 'Mobile coverage',
  query,
};
const inspected = applySourceSchema(draft, coverageSchema);

const meta = {
  title: 'Sources/DatasetForm',
  component: DatasetForm,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Defines a dataset in two steps. First the slug, name, connection and source query; then, once `schema` is supplied by the zero-row schema inspection, the geometry mapping and a table of result columns.',
          '',
          '- The query label, description and placeholder come from the connection adapter (`sourceInput`).',
          '- Geometry formats come from the adapter (`geometryKinds`): longitude/latitude and X/Y use two column selects, other formats use one.',
          '- Each result column can be kept as a channel with a role and a type; geometry columns and unsupported column types cannot. The table writes the channel JSON that is also editable under Advanced channel JSON.',
          '- Save stays disabled until a schema has been inspected.',
        ].join('\n'),
      },
    },
  },
  args: {
    form: draft,
    connections,
    geometryKinds: adapter.geometryKinds,
    sourceInput,
    errors: {},
    busy: false,
    inspecting: false,
    onChange: fn(),
    onInspect: fn(),
    onSubmit: fn((event) => event.preventDefault()),
  },
  argTypes: {
    form: { control: 'object', description: 'Current form values.' },
    connections: { control: 'object', description: 'Saved connections that can be selected.' },
    geometryKinds: { control: 'object', description: 'Geometry formats the adapter supports.' },
    sourceInput: { control: 'object', description: 'Label, description and placeholder of the query field.' },
    schema: { control: 'object', description: 'Result of the schema inspection; absent until it has run.' },
    errors: { control: 'object', description: 'Validation messages keyed by field.' },
    busy: { control: 'boolean', description: 'Disables the submit button and shows "Saving…".' },
    inspecting: { control: 'boolean', description: 'Disables the inspect button and shows "Inspecting…".' },
    onChange: { control: false },
    onInspect: { control: false },
    onSubmit: { control: false },
  },
} satisfies Meta<typeof DatasetForm>;

export default meta;
type Story = StoryObj<typeof meta>;

function FormHost({ form: initial, onChange, ...props }: FormProps) {
  const [form, setForm] = useState<DatasetFormState>(initial);
  return (
    <DatasetForm
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

export const BeforeInspection: Story = {
  render,
  play: async ({ args, canvas, userEvent }) => {
    await expect(canvas.getByRole('button', { name: 'Save dataset' })).toBeDisabled();
    await userEvent.click(canvas.getByRole('button', { name: 'Inspect query columns' }));
    await expect(args.onInspect).toHaveBeenCalled();
  },
};

export const Inspecting: Story = {
  render,
  args: { inspecting: true },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Inspecting…' })).toBeDisabled();
  },
};

export const AfterInspection: Story = {
  render,
  args: { form: inspected, schema: coverageSchema },
  parameters: { docs: { description: { story: 'The form after `applySourceSchema` has matched `lon` and `lat` as the geometry and suggested a channel for each other supported column.' } } },
  play: async ({ canvas, userEvent }) => {
    await expect(canvas.getByText('8 result columns')).toBeInTheDocument();
    await expect(canvas.getByRole('checkbox', { name: 'Use lon as a channel' })).toBeDisabled();
    await expect(canvas.getByRole('checkbox', { name: 'Use footprint as a channel' })).toBeDisabled();
    await userEvent.click(canvas.getByRole('checkbox', { name: 'Use tests as a channel' }));
    await expect(canvas.getByRole('checkbox', { name: 'Use tests as a channel' })).not.toBeChecked();
  },
};

export const WithErrors: Story = {
  render,
  args: {
    form: { ...inspected, slug: 'Mobile Coverage', name: '', channels: '[]' },
    schema: coverageSchema,
    errors: {
      slug: ['Use lowercase kebab-case.'],
      name: ['Enter a dataset name.'],
      channels: ['Select at least one channel.'],
    },
  },
};

export const Saving: Story = {
  render,
  args: { form: inspected, schema: coverageSchema, busy: true },
};

export const WellKnownText: Story = {
  render,
  args: {
    form: { ...inspected, geometryKind: 'wkt', geometryColumn: '' },
    geometryKinds: sourceAdapters[1].geometryKinds,
    schema: coverageSchema,
  },
  parameters: { docs: { description: { story: 'Non-point geometry formats use a single column select.' } } },
};
