import type { Meta, StoryObj } from '@storybook/react-vite';
import { coveragePreview } from '../../../../.storybook/fixtures/sources';
import { PreviewPanel } from './PreviewPanel';

const meta = {
  title: 'Sources/PreviewPanel',
  component: PreviewPanel,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'Shows the result of a bounded dataset preview: the sampled row count, distinct geometries, payload size, geometry bounds and the returned rows with their column types.',
          'The badge reads "Within budget" or, when the preview was cut short, "Row limit reached". Warnings from the source appear as warning badges; null cells render as an italic `null`.',
          'Without a `preview` it shows the limits of the preview as an empty state.',
        ].join('\n'),
      },
    },
  },
  args: { preview: coveragePreview, datasetName: 'Mobile coverage' },
  argTypes: {
    preview: { control: 'object', description: 'The preview response; absent before a preview has run.' },
    datasetName: { control: 'text', description: 'Name shown in the panel heading.' },
  },
} satisfies Meta<typeof PreviewPanel>;

export default meta;
type Story = StoryObj<typeof meta>;

export const WithinBudget: Story = {};

export const Truncated: Story = {
  args: {
    preview: {
      ...coveragePreview,
      truncated: true,
      resultBytes: 1_048_000,
      warnings: ['Rows beyond the preview limit were not returned.'],
    },
  },
};

export const NoPreviewYet: Story = {
  args: { preview: undefined, datasetName: undefined },
};
