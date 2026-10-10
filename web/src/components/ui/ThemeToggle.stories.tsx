import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import type { Theme } from '@/types/theme';
import ThemeToggle from './ThemeToggle';

const meta = {
  title: 'Components/ThemeToggle',
  component: ThemeToggle,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'An icon-only ghost button that flips between the dark and light themes. The icon and the accessible name describe the mode the click will activate: a sun while dark is active, a moon while light is active.',
          '',
          'The component is controlled. In the app, `ManagementLayout` wires it to `useTheme`, which writes `data-theme` on `<html>`.',
          'Here the `theme` prop only drives the label; the Storybook toolbar owns the real theme.',
        ].join('\n'),
      },
    },
  },
  args: { theme: 'light', onToggle: fn() },
  argTypes: {
    theme: {
      control: 'inline-radio',
      options: ['light', 'dark'] satisfies Theme[],
      description: 'The theme that is currently active.',
    },
    onToggle: { control: false, description: 'Called when the button is pressed.' },
  },
} satisfies Meta<typeof ThemeToggle>;

export default meta;
type Story = StoryObj<typeof meta>;

export const LightActive: Story = {};

export const DarkActive: Story = { args: { theme: 'dark' } };

function ToggleHost() {
  const [theme, setTheme] = useState<Theme>('light');
  return <ThemeToggle theme={theme} onToggle={() => setTheme((current) => (current === 'dark' ? 'light' : 'dark'))} />;
}

export const Interactive: Story = {
  render: () => <ToggleHost />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Switch to dark mode' }));
    await expect(canvas.getByRole('button', { name: 'Switch to light mode' })).toBeInTheDocument();
  },
};
