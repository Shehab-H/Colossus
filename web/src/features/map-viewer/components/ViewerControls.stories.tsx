import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { populationChannel, viewerChannels, viewerControls, viewerOptions } from '../../../../.storybook/fixtures/viewer';
import ViewerControls, { type ViewerControlsProps } from './ViewerControls';

const meta = {
  title: 'Map Viewer/ViewerControls',
  component: ViewerControls,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'The color selector plus one control per filterable channel. The control kind comes from `controls[channel.name]`:',
          '',
          '- `dateRange` renders two date inputs; `range` renders two number inputs. Both store the value as `from..to`.',
          '- any other value renders a select whose options are `options[channel.name]`, preceded by "(all)".',
          '',
          'The "Color by" selector is hidden when `colorChannels` is empty. Filter state is a map of channel name to string; "(all)" means no filter.',
        ].join('\n'),
      },
    },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
  args: {
    colorChannels: viewerChannels,
    colorChannel: 'feature_class',
    channels: viewerChannels,
    controls: viewerControls,
    options: viewerOptions,
    filters: {},
    onColorChannelChange: fn(),
    onFilterChange: fn(),
  },
  argTypes: {
    colorChannels: { control: 'object', description: 'Channels that can be used for color.' },
    colorChannel: { control: 'text', description: 'Name of the channel currently used for color.' },
    channels: { control: 'object', description: 'Channels that get a filter control.' },
    controls: { control: 'object', description: 'Control kind per channel name.' },
    options: { control: 'object', description: 'Selectable values per channel; for ranges the first and last entries are the bounds.' },
    filters: { control: 'object', description: 'Current filter value per channel name.' },
    onColorChannelChange: { control: false },
    onFilterChange: { control: false },
  },
} satisfies Meta<typeof ViewerControls>;

export default meta;
type Story = StoryObj<typeof meta>;

function ControlsHost({ filters: initial, onFilterChange, ...props }: ViewerControlsProps) {
  const [filters, setFilters] = useState(initial);
  return (
    <ViewerControls
      {...props}
      filters={filters}
      onFilterChange={(name, value) => {
        onFilterChange(name, value);
        setFilters((current) => ({ ...current, [name]: value }));
      }}
    />
  );
}

const render: Story['render'] = (args) => <ControlsHost {...args} />;

export const Default: Story = {
  render,
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.selectOptions(canvas.getByRole('combobox', { name: /country_code/ }), 'EG');
    await expect(args.onFilterChange).toHaveBeenCalledWith('country_code', 'EG');
  },
};

export const WithNumericRange: Story = {
  render,
  args: {
    channels: [...viewerChannels, populationChannel],
    controls: { ...viewerControls, population: 'range' },
    options: { ...viewerOptions, population: ['0', '9500000'] },
    filters: { population: '1000..500000' },
  },
};

export const WithActiveFilters: Story = {
  render,
  args: { filters: { feature_class: 'P', country_code: 'GB', modification_date: '2026-03-01..2026-06-30' } },
};

export const WithoutColorSelector: Story = {
  render,
  args: { colorChannels: [] },
};
