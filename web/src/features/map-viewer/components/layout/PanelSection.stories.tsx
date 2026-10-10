import type { Meta, StoryObj } from '@storybook/react-vite';
import { PanelSection } from './PanelSection';

const meta = {
  title: 'Map Viewer/Layout/PanelSection',
  component: PanelSection,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: 'A titled block inside a viewer panel: an eyebrow, a heading (`<h2>`) and the section content. Consecutive sections are separated by a divider.',
      },
    },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
  args: {
    eyebrow: 'Visible data',
    title: 'Legend',
    children: <p>Section content is rendered here.</p>,
  },
  argTypes: {
    eyebrow: { control: 'text', description: 'Small uppercase label above the heading.' },
    title: { control: 'text', description: 'Section heading, rendered as an `<h2>`.' },
    children: { control: false },
  },
} satisfies Meta<typeof PanelSection>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Single: Story = {};

export const Stacked: Story = {
  render: (args) => (
    <>
      <PanelSection {...args} />
      <PanelSection eyebrow="Pinned mark" title="Inspection"><p>A second section gains a top divider.</p></PanelSection>
    </>
  ),
};
