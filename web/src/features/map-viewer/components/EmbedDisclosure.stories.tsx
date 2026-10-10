import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { buildEmbedUrl, embedSnippet } from '../embed/embedUrl';
import EmbedDisclosure from './EmbedDisclosure';

const getEmbed = fn(() => {
  const url = buildEmbedUrl('https://maps.example.com/maps/network-coverage', {
    view: 'network-coverage',
    color: 'network',
    theme: 'light',
    camera: { longitude: 31.2357, latitude: 30.0444, zoom: 5 },
    filters: { country_code: 'EG', feature_class: '(all)' },
  });
  return { url, snippet: embedSnippet(url) };
});

const meta = {
  title: 'Map Viewer/EmbedDisclosure',
  component: EmbedDisclosure,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'A collapsible "Embed this map" section. `getEmbed` is called only when the user presses `Generate iframe`, so the URL reflects the map state at that moment.',
          'The result is shown as a read-only snippet with `Copy iframe` and `Open embed` actions. In these stories `getEmbed` builds the URL with `buildEmbedUrl` and `embedSnippet` from `embed/embedUrl.ts`.',
        ].join('\n'),
      },
    },
  },
  decorators: [(Story) => <div className="story-narrow story-frame"><Story /></div>],
  args: { getEmbed },
  argTypes: {
    getEmbed: { control: false, description: 'Returns the embed URL and the iframe snippet for the current map state.' },
  },
} satisfies Meta<typeof EmbedDisclosure>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Collapsed: Story = {};

export const Generated: Story = {
  parameters: { docs: { description: { story: 'Expanded, with the snippet generated. Only filters that are set are carried into the URL; "(all)" is dropped.' } } },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByText('Embed this map'));
    await userEvent.click(canvas.getByRole('button', { name: 'Generate iframe' }));
    const snippet = (canvas.getByRole('textbox', { name: 'Iframe embed code' }) as HTMLTextAreaElement).value;
    await expect(snippet).toContain('<iframe src="https://maps.example.com/embed/network-coverage?');
    await expect(snippet).toContain('f_country_code=EG');
    await expect(snippet).not.toContain('f_feature_class');
    await expect(canvas.getByRole('link', { name: 'Open embed' })).toHaveAttribute('target', '_blank');
  },
};
