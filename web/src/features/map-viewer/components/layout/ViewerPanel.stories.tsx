import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { PanelSection } from './PanelSection';
import { ViewerPanel } from './ViewerPanel';

type PanelProps = Parameters<typeof ViewerPanel>[0];

const meta = {
  title: 'Map Viewer/Layout/ViewerPanel',
  component: ViewerPanel,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'A collapsible side panel of the viewer shell. The toggle button reads "Hide" while open and "Show" while collapsed, followed by the lowercased `label` (for example "Hide map controls"), and exposes `aria-expanded`. The panel itself is labelled with `label` as given.',
          'The body is rendered only while the panel is open. `side` decides the grid area (`left` or `right`) and the direction of the chevron.',
          'The parent owns `open`; `ViewerShell` narrows its matching grid column when a panel collapses.',
        ].join('\n'),
      },
    },
  },
  decorators: [(Story) => <div className="story-panel-frame"><Story /></div>],
  args: {
    side: 'left',
    open: true,
    label: 'Map controls',
    onToggle: fn(),
    children: (
      <PanelSection eyebrow="Map controls" title="Layers & filters">
        <p>Panel content is rendered here.</p>
      </PanelSection>
    ),
  },
  argTypes: {
    side: { control: 'inline-radio', options: ['left', 'right'], description: 'Which side of the shell the panel sits on.' },
    open: { control: 'boolean', description: 'Whether the body is shown.' },
    label: { control: 'text', description: 'Panel name, used for `aria-label` and the toggle text.' },
    onToggle: { control: false, description: 'Called when the toggle is pressed.' },
    children: { control: false },
  },
} satisfies Meta<typeof ViewerPanel>;

export default meta;
type Story = StoryObj<typeof meta>;

function PanelHost({ open: initial, onToggle, ...props }: PanelProps) {
  const [open, setOpen] = useState(initial);
  return <ViewerPanel {...props} open={open} onToggle={() => { onToggle(); setOpen((current) => !current); }} />;
}

const render: Story['render'] = (args) => <PanelHost {...args} />;

export const LeftOpen: Story = {
  render,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Hide map controls' }));
    await expect(canvas.getByRole('button', { name: 'Show map controls' })).toHaveAttribute('aria-expanded', 'false');
  },
};

export const LeftCollapsed: Story = { render, args: { open: false } };

export const RightOpen: Story = {
  render,
  args: { side: 'right', label: 'Map details' },
};

export const RightCollapsed: Story = {
  render,
  args: { side: 'right', label: 'Map details', open: false },
};
