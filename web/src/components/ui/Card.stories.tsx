import type { Meta, StoryObj } from '@storybook/react-vite';
import { Card, PaddedCard } from './Card';

const meta = {
  title: 'Components/Card',
  component: Card,
  subcomponents: { PaddedCard },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: '`Card` is a bordered surface with a small shadow and no padding, for content that manages its own spacing such as tables. `PaddedCard` adds the `ui-card--padded` modifier for ordinary content. Both render a `<div>` and pass remaining props through.',
      },
    },
  },
  argTypes: {
    className: { control: false, description: 'Extra classes appended to the generated ones.' },
    children: { control: false },
  },
} satisfies Meta<typeof Card>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Unpadded: Story = {
  render: (args) => (
    <Card {...args}>
      <table className="story-table">
        <caption className="ui-sr-only">Example rows</caption>
        <tbody>
          <tr><th scope="row">Map</th><td>Network coverage</td></tr>
          <tr><th scope="row">State</th><td>Published</td></tr>
        </tbody>
      </table>
    </Card>
  ),
};

export const Padded: Story = {
  render: (args) => (
    <PaddedCard {...args}>
      <strong>Release v20260814</strong>
      <p>Padded cards wrap ordinary content with the standard inner spacing.</p>
    </PaddedCard>
  ),
};
