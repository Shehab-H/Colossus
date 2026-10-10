import type { Meta, StoryObj } from '@storybook/react-vite';
import { Button } from './Button';
import { Alert, EmptyState, LoadingState } from './Feedback';

const tones = ['info', 'warning', 'danger'] as const;

const meta = {
  title: 'Components/Feedback',
  component: Alert,
  subcomponents: { LoadingState, EmptyState },
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Inline responses to system state.',
          '',
          '- `Alert` has the tones `info` (default), `warning` and `danger`. `danger` uses `role="alert"`; the others use `role="status"`.',
          '- `LoadingState` announces its text politely (`aria-live="polite"`) and defaults to "Loading…".',
          '- `EmptyState` is a section with an optional eyebrow, a title, a description and an optional action.',
        ].join('\n'),
      },
    },
  },
  args: { children: 'Release v20260814 is ready to publish.' },
  argTypes: {
    tone: {
      control: 'inline-radio',
      options: tones,
      description: 'Severity of the message; it also selects the ARIA role.',
      table: { type: { summary: tones.map((value) => `'${value}'`).join(' | ') }, defaultValue: { summary: "'info'" } },
    },
    className: { control: false, description: 'Extra classes appended to the generated ones.' },
  },
} satisfies Meta<typeof Alert>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const AlertTones: Story = {
  render: () => (
    <div className="story-stack">
      <Alert>Release v20260814 is ready to publish.</Alert>
      <Alert tone="warning">The schedule is disabled, so no new release will be baked.</Alert>
      <Alert tone="danger">The source query could not be validated.</Alert>
    </div>
  ),
};

export const Loading: Story = {
  render: () => <LoadingState />,
};

export const LoadingWithMessage: Story = {
  render: () => <LoadingState>Loading map catalog…</LoadingState>,
};

export const Empty: Story = {
  render: () => (
    <EmptyState
      eyebrow="No releases"
      title="Publish the first release"
      description="Run a verified bake to make this map available to viewers."
      action={<Button variant="primary">Run bake</Button>}
    />
  ),
};

export const EmptyWithoutAction: Story = {
  render: () => (
    <EmptyState title="No connections yet" description="Add a source connection to start discovering datasets." />
  ),
};
