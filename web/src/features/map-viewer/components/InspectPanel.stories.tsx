import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import InspectPanel from './InspectPanel';

const rows = [
  { name: 'signal_strength', value: '-63 dBm' },
  { name: 'network', value: '5G' },
  { name: 'samples', value: '42,901' },
];

const meta = {
  title: 'Map Viewer/InspectPanel',
  component: InspectPanel,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'The pinned read-out of one clicked mark. It lists the inspection channels configured for the map as name and value rows, under a title.',
          'The title is the channel chosen as the panel title, or "Cell" when the selection has none. The close button has the accessible name "Close inspection".',
        ].join('\n'),
      },
    },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
  args: { selection: { title: 'Cairo · EG', rows }, onClose: fn() },
  argTypes: {
    selection: { control: 'object', description: 'Optional title and the rows to show.' },
    onClose: { control: false, description: 'Called when the close button is pressed.' },
  },
} satisfies Meta<typeof InspectPanel>;

export default meta;
type Story = StoryObj<typeof meta>;

export const WithTitle: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Close inspection' }));
    await expect(args.onClose).toHaveBeenCalled();
  },
};

export const WithoutTitle: Story = { args: { selection: { rows } } };
