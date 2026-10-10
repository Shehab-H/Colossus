import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { coverageMapDraft } from '../../../../.storybook/fixtures/maps';
import { coverageDataset } from '../../../../.storybook/fixtures/sources';
import { emptyMapForm, formFromDraft, type MapFormState } from '../forms/mapForm';
import { MapConfigurationBuilder } from './MapConfigurationBuilder';

type BuilderProps = Parameters<typeof MapConfigurationBuilder>[0];

const meta = {
  title: 'Maps/MapConfigurationBuilder',
  component: MapConfigurationBuilder,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'The guided editor for the configuration of a map. It edits a `MapFormState` whose JSON fields (`bakeFilters`, `filters`, `measures`, `encoding`, `inspect`) hold strings, and writes every change back through `onChange(field, value)`.',
          '',
          'Sections: bake-time row predicates, interactive filters (automatic until a curated list is written), computed measures, visual encoding (see ColorEncodingEditor), click inspection and the advanced JSON fields.',
          'Without a `dataset` it renders only a prompt to select one. Filter controls are offered per channel: date range for temporal channels, single select for dictionary dimensions and numeric range for the other numeric channels.',
        ].join('\n'),
      },
    },
  },
  args: {
    form: emptyMapForm(),
    dataset: coverageDataset,
    errors: {},
    onChange: fn(),
  },
  argTypes: {
    form: { control: 'object', description: 'Current form values; the JSON fields are strings.' },
    dataset: { control: 'object', description: 'The selected dataset, which supplies the channels.' },
    errors: { control: 'object', description: 'Validation messages keyed by form field.' },
    onChange: { control: false, description: 'Called with a field name and its new value.' },
  },
} satisfies Meta<typeof MapConfigurationBuilder>;

export default meta;
type Story = StoryObj<typeof meta>;

function BuilderHost({ form: initial, onChange, ...props }: BuilderProps) {
  const [form, setForm] = useState<MapFormState>(initial);
  return (
    <MapConfigurationBuilder
      {...props}
      form={form}
      onChange={(field, value) => {
        onChange(field, value);
        setForm((current) => ({ ...current, [field]: value }));
      }}
    />
  );
}

const render: Story['render'] = (args) => <BuilderHost {...args} />;

export const AutomaticControls: Story = {
  render,
  parameters: { docs: { description: { story: 'A new map: no explicit filter list, so the categorical and temporal channels are exposed automatically.' } } },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getAllByRole('checkbox', { name: /signal_strength/ })[0]);
    await expect(args.onChange).toHaveBeenCalledWith('filters', expect.stringContaining('signal_strength'));
  },
};

export const CuratedConfiguration: Story = {
  render,
  args: { form: formFromDraft(coverageMapDraft) },
  parameters: { docs: { description: { story: 'An existing map with an explicit filter list, two computed measures, a categorical color encoding and an inspection list.' } } },
};

export const WithErrors: Story = {
  render,
  args: {
    form: formFromDraft(coverageMapDraft),
    errors: {
      bakeFilters: ['Bake filters must be a list of SQL predicates.'],
      filters: ['Filters must be a JSON array.'],
      measures: ['Measures must be a JSON array.'],
      encoding: ['Colors must use #RGB or #RRGGBB hexadecimal notation.'],
      inspect: ['Inspection channels must exist in the dataset.'],
    },
  },
};

export const InvalidEncodingJson: Story = {
  render,
  args: { form: { ...formFromDraft(coverageMapDraft), encoding: '{ "color": ' } },
  parameters: { docs: { description: { story: 'Unparseable encoding JSON replaces the guided color controls with an error until it is repaired under Advanced configuration JSON.' } } },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/Encoding JSON is invalid/)).toBeInTheDocument();
  },
};

export const NoDataset: Story = {
  render,
  args: { dataset: undefined },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/Select a dataset to configure/)).toBeInTheDocument();
  },
};
