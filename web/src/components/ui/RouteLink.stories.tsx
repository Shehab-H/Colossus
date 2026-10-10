import type { Meta, StoryObj } from '@storybook/react-vite';
import { ButtonRouteLink, RouteLink } from './RouteLink';

const meta = {
  title: 'Components/RouteLink',
  component: RouteLink,
  subcomponents: { ButtonRouteLink },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          '`RouteLink` renders React Router\'s `Link` and passes every prop through.',
          '`ButtonRouteLink` renders the same link with the button classes and accepts the `variant`, `size` and `iconOnly` style props.',
          '',
          'Both need a router; stories run inside a memory router, so the links navigate within the story without leaving it.',
          'Neither is re-exported from `components/ui/index.ts`; import them from `@/components/ui/RouteLink`.',
        ].join('\n'),
      },
    },
  },
  args: { to: '/maps', children: 'All maps' },
  argTypes: {
    to: { control: 'text', description: 'Target path.' },
    className: { control: false },
  },
} satisfies Meta<typeof RouteLink>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Link: Story = {};

export const ButtonLinks: Story = {
  render: () => (
    <div className="story-row">
      <ButtonRouteLink variant="primary" to="/maps/new">Create map</ButtonRouteLink>
      <ButtonRouteLink variant="secondary" to="/maps">Secondary</ButtonRouteLink>
      <ButtonRouteLink variant="ghost" size="sm" to="/sources">Sources</ButtonRouteLink>
      <ButtonRouteLink variant="danger" size="lg" to="/maps">Large</ButtonRouteLink>
    </div>
  ),
};
