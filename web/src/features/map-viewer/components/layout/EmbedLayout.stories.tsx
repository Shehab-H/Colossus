import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect } from 'storybook/test';
import { viewerChannels, viewerControls, viewerOptions, gradientLegend } from '../../../../../.storybook/fixtures/viewer';
import Legend from '../Legend';
import ViewerControls from '../ViewerControls';
import { EmbedShell } from './EmbedShell';
import { EmbedSidebar } from './EmbedSidebar';
import { EmbedStage } from './EmbedStage';

const meta = {
  title: 'Map Viewer/Layout/EmbedLayout',
  parameters: {
    docs: {
      description: {
        component: [
          'The chrome-free layout used by `/embed/:mapId`: `EmbedShell` is a three-column grid (`controls`, `stage`, `details`), `EmbedSidebar` is a collapsible side panel and `EmbedStage` holds the map with a status footer carrying the map title.',
          'Below 56.25rem the shell stacks the stage above the sidebars.',
        ].join('\n'),
      },
    },
  },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

function EmbedWorkspace() {
  const [controlsOpen, setControlsOpen] = useState(true);
  const [detailsOpen, setDetailsOpen] = useState(true);
  const [colorChannel, setColorChannel] = useState('feature_class');
  return (
    <EmbedShell>
      <EmbedSidebar side="left" open={controlsOpen} onToggle={() => setControlsOpen((open) => !open)} label="Controls">
        <ViewerControls
          colorChannels={viewerChannels}
          colorChannel={colorChannel}
          onColorChannelChange={setColorChannel}
          channels={viewerChannels}
          controls={viewerControls}
          options={viewerOptions}
          filters={{}}
          onFilterChange={() => undefined}
        />
      </EmbedSidebar>
      <EmbedStage
        map={<div className="workbench-map map-canvas" role="img" aria-label="Map preview"><div className="workbench-map__land" aria-hidden="true" /></div>}
        title="Global network coverage"
        status={<span>4 tiles</span>}
      />
      <EmbedSidebar side="right" open={detailsOpen} onToggle={() => setDetailsOpen((open) => !open)} label="Details">
        <Legend legend={gradientLegend} />
      </EmbedSidebar>
    </EmbedShell>
  );
}

export const Embed: Story = {
  render: () => <EmbedWorkspace />,
  play: async ({ canvas, userEvent }) => {
    const toggle = canvas.getByRole('button', { name: 'Controls' });
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await userEvent.click(toggle);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  },
};
