import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { connections, coverageDataset, datasets } from '../../../../.storybook/fixtures/sources';
import { DatasetList } from './DatasetList';

const meta = {
  title: 'Sources/DatasetList',
  component: DatasetList,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Lists the saved datasets with their connection adapter, geometry kind and channel count, and a `Run bounded preview` button per dataset.',
          'The selected dataset\'s button uses the primary variant; the dataset being previewed shows "Previewing…" and is disabled.',
          'A dataset whose connection is missing from `connections` shows an `unknown` adapter and "Missing connection". Without datasets the list renders an empty state.',
        ].join('\n'),
      },
    },
  },
  args: { datasets, connections, onPreview: fn() },
  argTypes: {
    datasets: { control: 'object', description: 'Saved datasets.' },
    connections: { control: 'object', description: 'Saved connections, used to label each dataset.' },
    previewingId: { control: 'text', description: 'Id of the dataset whose preview is running.' },
    selectedId: { control: 'text', description: 'Id of the dataset whose preview is shown.' },
    onPreview: { control: false },
  },
} satisfies Meta<typeof DatasetList>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getAllByRole('button', { name: 'Run bounded preview' })[0]);
    await expect(args.onPreview).toHaveBeenCalledWith(coverageDataset.id);
  },
};

export const Selected: Story = {
  args: { selectedId: coverageDataset.id },
};

export const Previewing: Story = {
  args: { previewingId: coverageDataset.id },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Previewing…' })).toBeDisabled();
  },
};

export const MissingConnection: Story = {
  args: { connections: [] },
};

export const Empty: Story = {
  args: { datasets: [] },
};
