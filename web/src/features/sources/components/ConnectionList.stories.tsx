import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { connectionValidation, connections, sourceLocations } from '../../../../.storybook/fixtures/sources';
import { ConnectionList } from './ConnectionList';

const meta = {
  title: 'Sources/ConnectionList',
  component: ConnectionList,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Lists the saved connections with their adapter, and two actions per connection: `Test connection` and `Discover locations`.',
          'The result of the last action is passed in from the parent, which keeps one validation and one location result at a time; it renders under the connection it belongs to.',
          'Without connections the list renders an empty state.',
        ].join('\n'),
      },
    },
  },
  args: { connections, onTest: fn(), onDiscover: fn() },
  argTypes: {
    connections: { control: 'object', description: 'Saved connections.' },
    testingId: { control: 'text', description: 'Id of the connection being tested.' },
    discoveringId: { control: 'text', description: 'Id of the connection being scanned for locations.' },
    validation: { control: 'object', description: 'Last validation result and the connection it belongs to.' },
    locations: { control: 'object', description: 'Last discovery result and the connection it belongs to.' },
    onTest: { control: false },
    onDiscover: { control: false },
  },
} satisfies Meta<typeof ConnectionList>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getAllByRole('button', { name: 'Test connection' })[0]);
    await expect(args.onTest).toHaveBeenCalledWith(connections[0].id);
  },
};

export const Testing: Story = {
  args: { testingId: connections[0].id },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Testing…' })).toBeDisabled();
  },
};

export const Discovering: Story = {
  args: { discoveringId: connections[1].id },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Discovering…' })).toBeDisabled();
  },
};

export const WithValidationResult: Story = {
  args: { validation: { connectionId: connections[0].id, result: connectionValidation } },
};

export const WithLocations: Story = {
  args: { locations: { connectionId: connections[0].id, result: sourceLocations } },
};

export const NoLocations: Story = {
  args: { locations: { connectionId: connections[1].id, result: [] } },
};

export const Empty: Story = {
  args: { connections: [] },
};
