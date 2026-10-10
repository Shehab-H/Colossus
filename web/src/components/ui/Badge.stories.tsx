import type { Meta, StoryObj } from '@storybook/react-vite';
import { Badge, type BadgeTone } from './Badge';

const tones: BadgeTone[] = ['neutral', 'brand', 'success', 'warning', 'danger', 'info'];

const meta = {
  title: 'Components/Badge',
  component: Badge,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: 'A compact pill for a status or category. `neutral` is the default and adds no modifier class; every other tone adds `ui-badge--<tone>`. Remaining props are passed to the `<span>`.',
      },
    },
  },
  args: { children: 'Published', tone: 'success' },
  argTypes: {
    tone: {
      control: 'select',
      options: tones,
      description: 'Semantic color of the badge.',
      table: { type: { summary: tones.map((value) => `'${value}'`).join(' | ') }, defaultValue: { summary: "'neutral'" } },
    },
    className: { control: false, description: 'Extra classes appended to the generated ones.' },
  },
} satisfies Meta<typeof Badge>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Tones: Story = {
  parameters: { docs: { description: { story: 'Every tone, as used for publication state, run status and adapter labels.' } } },
  render: () => (
    <div className="story-row">
      {tones.map((tone) => <Badge key={tone} tone={tone}>{tone}</Badge>)}
    </div>
  ),
};
