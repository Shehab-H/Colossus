import type { Meta, StoryObj } from '@storybook/react-vite';
import { Button } from '@/components/ui';
import { ViewerHeader } from './ViewerHeader';

const meta = {
  title: 'Map Viewer/Layout/ViewerHeader',
  component: ViewerHeader,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: 'The viewer title bar: an eyebrow and the map title (an `<h1>`) on the left, and on the right an optional release label in monospace followed by any actions. In the real shell it occupies the `header` grid area.',
      },
    },
  },
  args: { eyebrow: 'Colossus Maps', title: 'Global network coverage', release: 'v20260814T090000Z' },
  argTypes: {
    eyebrow: { control: 'text', description: 'Small uppercase label above the title.' },
    title: { control: 'text', description: 'Map title, rendered as an `<h1>`.' },
    release: { control: 'text', description: 'Release version label; omitted when absent.' },
    actions: { control: false, description: 'Controls placed after the release label.' },
  },
} satisfies Meta<typeof ViewerHeader>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const WithActions: Story = {
  args: { actions: <Button variant="ghost" size="sm">Share</Button> },
};

export const WithoutRelease: Story = {
  args: { release: undefined },
};
