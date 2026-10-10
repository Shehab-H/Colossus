import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, fn } from 'storybook/test';
import { catalogMaps, publishedMap } from '../../../../.storybook/fixtures/maps';
import MapCatalogTable from './MapCatalogTable';

const meta = {
  title: 'Maps/MapCatalogTable',
  component: MapCatalogTable,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'The catalog of managed maps. Each row shows the source adapter, a publication-state badge, the current release, the last run, the next scheduled run and the output size.',
          '',
          '- Row actions: `Open` (the public viewer), `Details`, `Edit` and `Archive`. Archived rows are dimmed and offer only `Details`.',
          '- `busyId` disables the `Archive` button of the row that is being archived.',
          '- A failed last run shows its failure message under the map title.',
          '- Below 62rem the table becomes a list of cards, and below 34rem the cards use a single column.',
        ].join('\n'),
      },
    },
  },
  args: { maps: catalogMaps, busyId: null, onArchive: fn() },
  argTypes: {
    maps: { control: 'object', description: 'Rows to render, as returned by `GET /api/maps`.' },
    busyId: { control: 'text', description: 'Id of the map whose archive request is in flight, or `null`.' },
    onArchive: { control: false, description: 'Called with the map when its `Archive` button is pressed.' },
  },
} satisfies Meta<typeof MapCatalogTable>;

export default meta;
type Story = StoryObj<typeof meta>;

export const AllStates: Story = {
  parameters: { docs: { description: { story: 'One row for each publication state: published, queued, baking, last run failed, archived and never baked.' } } },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getAllByRole('button', { name: 'Archive' })[0]);
    await expect(args.onArchive).toHaveBeenCalledWith(publishedMap);
  },
};

export const ArchiveInProgress: Story = {
  args: { busyId: publishedMap.id },
  parameters: { docs: { description: { story: 'The archive button of the busy row is disabled.' } } },
  play: async ({ canvas }) => {
    await expect(canvas.getAllByRole('button', { name: 'Archive' })[0]).toBeDisabled();
  },
};

export const MobileLayout: Story = {
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  parameters: { docs: { description: { story: 'At phone widths every row becomes a card and the column names are shown as captions.' } } },
};
