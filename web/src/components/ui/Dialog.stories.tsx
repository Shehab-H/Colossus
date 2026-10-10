import { useState, type ComponentProps } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { Button } from './Button';
import { ConfirmDialog } from './Dialog';

const meta = {
  title: 'Components/Dialog',
  component: ConfirmDialog,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          '`ConfirmDialog` wraps a native `<dialog>` and opens it with `showModal()` whenever `open` becomes true. The browser then renders it in the top layer and makes the rest of the document inert.',
          'Pressing Escape fires the `cancel` event of the dialog. The component prevents the default close and calls `onCancel`, so the parent stays the single owner of the `open` state.',
          '',
          '`tone` styles the confirm button (`danger` by default); `busy` disables both buttons and replaces the confirm label with "Working…".',
        ].join('\n'),
      },
    },
  },
  args: {
    open: false,
    title: 'Archive Network coverage?',
    description: 'The map will disappear from the active catalog. Published artifacts remain immutable.',
    confirmLabel: 'Archive map',
    onConfirm: fn(),
    onCancel: fn(),
  },
  argTypes: {
    open: { control: 'boolean', description: 'Whether the dialog is shown as a modal.' },
    title: { control: 'text', description: 'Heading of the dialog.' },
    description: { control: 'text', description: 'Explanatory copy under the heading.' },
    confirmLabel: { control: 'text', description: 'Label of the confirm button.', table: { defaultValue: { summary: "'Confirm'" } } },
    cancelLabel: { control: 'text', description: 'Label of the cancel button.', table: { defaultValue: { summary: "'Cancel'" } } },
    tone: {
      control: 'inline-radio',
      options: ['primary', 'danger'],
      description: 'Button variant of the confirm action.',
      table: { defaultValue: { summary: "'danger'" } },
    },
    busy: { control: 'boolean', description: 'Disables both buttons while the confirmed action runs.', table: { defaultValue: { summary: 'false' } } },
    onConfirm: { control: false },
    onCancel: { control: false },
  },
} satisfies Meta<typeof ConfirmDialog>;

export default meta;
type Story = StoryObj<typeof meta>;

function ConfirmationHost(args: ComponentProps<typeof ConfirmDialog>) {
  const [open, setOpen] = useState(false);
  return (
    <div className="story-frame">
      <Button variant="danger" onClick={() => setOpen(true)}>{args.confirmLabel}</Button>
      <ConfirmDialog
        {...args}
        open={open}
        onConfirm={() => { args.onConfirm(); setOpen(false); }}
        onCancel={() => { args.onCancel(); setOpen(false); }}
      />
    </div>
  );
}

export const ConfirmationFlow: Story = {
  render: (args) => <ConfirmationHost {...args} />,
  parameters: { docs: { description: { story: 'The trigger owns the `open` state. Cancelling closes the dialog and leaves the page unchanged.' } } },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Archive map' }));
    await expect(canvas.getByRole('dialog')).toBeInTheDocument();
    await userEvent.click(canvas.getByRole('button', { name: 'Cancel' }));
    await expect(canvas.queryByRole('dialog')).not.toBeInTheDocument();
  },
};

export const Open: Story = {
  args: { open: true },
  parameters: { docs: { story: { inline: false, iframeHeight: 360 } } },
};

export const PrimaryTone: Story = {
  args: {
    open: true,
    tone: 'primary',
    title: 'Publish this release?',
    description: 'Viewers will switch to the new release as soon as it is published.',
    confirmLabel: 'Publish',
  },
  parameters: { docs: { story: { inline: false, iframeHeight: 360 } } },
};

export const Busy: Story = {
  args: { open: true, busy: true },
  parameters: { docs: { story: { inline: false, iframeHeight: 360 } } },
};
