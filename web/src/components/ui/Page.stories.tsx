import type { Meta, StoryObj } from '@storybook/react-vite';
import { Button } from './Button';
import { PageHeader, StatCard } from './Page';

const meta = {
  title: 'Components/Page',
  component: PageHeader,
  subcomponents: { StatCard },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Building blocks for management pages.',
          '',
          '- `PageHeader` renders the page `<h1>` with an optional eyebrow, description and action area. Below 44rem the header stacks and its actions fill the row.',
          '- `StatCard` is a compact label, value and note read-out; `danger` tints its border.',
        ].join('\n'),
      },
    },
  },
  args: {
    eyebrow: 'Control plane',
    title: 'Maps',
    description: 'Manage map definitions, bake runs, releases, schedules, and archived records.',
  },
  argTypes: {
    eyebrow: { control: 'text', description: 'Small uppercase label above the title.' },
    title: { control: 'text', description: 'Page heading, rendered as an `<h1>`.' },
    description: { control: 'text', description: 'Supporting copy under the title.' },
    actions: { control: false, description: 'Buttons or links shown to the right of the title.' },
    className: { control: false, description: 'Extra classes on the header element.' },
  },
} satisfies Meta<typeof PageHeader>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Header: Story = {};

export const HeaderWithActions: Story = {
  args: {
    actions: (
      <>
        <Button variant="ghost">Sources</Button>
        <Button variant="primary">Create map</Button>
      </>
    ),
  },
};

export const StatCards: Story = {
  parameters: { docs: { description: { story: '`StatCard` truncates long values and notes with an ellipsis.' } } },
  render: () => (
    <div className="story-grid">
      <StatCard label="Published release" value="v20260814" note="12 minutes ago" />
      <StatCard label="Visible cells" value="13.2M" note="4 tiles in viewport" />
      <StatCard label="Last run" value="Failed" note="Source timeout" danger />
    </div>
  ),
};
