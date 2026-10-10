import type { Meta, StoryObj } from '@storybook/react-vite';
import { binnedLegend, categoricalLegend, clampedLegend, gradientLegend } from '../../../../.storybook/fixtures/viewer';
import Legend from './Legend';

const meta = {
  title: 'Map Viewer/Legend',
  component: Legend,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Describes the active color scale. The `legend` model comes from `describeLegend` in `lib/color/legend.ts`, which resolves the same scale the GPU color lookup uses, so the legend and the marks cannot disagree.',
          '',
          '- `continuous` legends draw a gradient bar with min, optional midpoint and max labels. A clamped end is marked with `≤` or `+`.',
          '- `binned` and `categorical` legends list labelled swatches; a categorical legend shows `+N more` when categories were cut off.',
        ].join('\n'),
      },
    },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
  args: { legend: gradientLegend },
  argTypes: {
    legend: { control: 'object', description: 'The structured legend description.' },
  },
} satisfies Meta<typeof Legend>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Gradient: Story = {};

export const GradientWithClampedEnds: Story = { args: { legend: clampedLegend } };

export const Binned: Story = { args: { legend: binnedLegend } };

export const Categorical: Story = { args: { legend: categoricalLegend } };
