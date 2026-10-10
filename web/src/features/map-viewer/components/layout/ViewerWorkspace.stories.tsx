import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect } from 'storybook/test';
import { Badge, Button } from '@/components/ui';
import { viewerChannels, viewerControls, viewerOptions, gradientLegend } from '../../../../../.storybook/fixtures/viewer';
import InspectPanel from '../InspectPanel';
import Legend from '../Legend';
import ViewerControls from '../ViewerControls';
import { MapWindow } from './MapWindow';
import { PanelSection } from './PanelSection';
import { ViewerHeader } from './ViewerHeader';
import { ViewerPanel } from './ViewerPanel';
import { ViewerShell } from './ViewerShell';

const meta = {
  title: 'Map Viewer/Workspace',
  parameters: {
    docs: {
      description: {
        component: 'The viewer shell composed from its layout pieces: header, two collapsible panels, the map window and its status bar. The map itself is a static placeholder; the real canvas needs tiles from a release.',
      },
    },
  },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

function ViewerWorkspace() {
  const [controlsOpen, setControlsOpen] = useState(true);
  const [detailsOpen, setDetailsOpen] = useState(true);
  const [inspectionOpen, setInspectionOpen] = useState(true);
  const [colorChannel, setColorChannel] = useState('feature_class');
  const [filters, setFilters] = useState<Record<string, string>>({});

  return (
    <ViewerShell controlsOpen={controlsOpen} detailsOpen={detailsOpen}>
      <ViewerHeader
        eyebrow="Colossus Maps"
        title="Global network coverage"
        release="v20260814T090000Z"
        actions={<Button variant="ghost" size="sm">Share</Button>}
      />
      <ViewerPanel side="left" open={controlsOpen} onToggle={() => setControlsOpen((open) => !open)} label="Map controls">
        <PanelSection eyebrow="Map controls" title="Layers & filters">
          <ViewerControls
            colorChannels={viewerChannels}
            colorChannel={colorChannel}
            onColorChannelChange={setColorChannel}
            channels={viewerChannels}
            controls={viewerControls}
            options={viewerOptions}
            filters={filters}
            onFilterChange={(name, value) => setFilters((current) => ({ ...current, [name]: value }))}
          />
        </PanelSection>
      </ViewerPanel>
      <MapWindow
        map={<div className="workbench-map map-canvas" role="img" aria-label="Map preview"><div className="workbench-map__land" aria-hidden="true" /></div>}
        status={<><span>4 tiles</span><span>128,212 cells resident</span></>}
      />
      <ViewerPanel side="right" open={detailsOpen} onToggle={() => setDetailsOpen((open) => !open)} label="Map details">
        <PanelSection eyebrow="Visible data" title="Legend">
          <Legend legend={gradientLegend} />
        </PanelSection>
        <PanelSection eyebrow="Pinned mark" title="Inspection">
          {inspectionOpen ? (
            <InspectPanel selection={{ title: 'Cairo · EG', rows: [
              { name: 'signal_strength', value: '-63 dBm' },
              { name: 'network', value: '5G' },
              { name: 'samples', value: '42,901' },
            ] }} onClose={() => setInspectionOpen(false)} />
          ) : <p className="viewer-inspection-empty">No pinned mark selected.</p>}
        </PanelSection>
        <Badge tone="success">Release verified</Badge>
      </ViewerPanel>
    </ViewerShell>
  );
}

export const Workspace: Story = { render: () => <ViewerWorkspace /> };

export const InteractionStates: Story = {
  render: () => <ViewerWorkspace />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Hide map controls' }));
    await expect(canvas.getByRole('button', { name: 'Show map controls' })).toBeInTheDocument();
    await userEvent.click(canvas.getByRole('button', { name: 'Close inspection' }));
    await expect(canvas.queryByRole('button', { name: 'Close inspection' })).not.toBeInTheDocument();
    await expect(canvas.getByText('No pinned mark selected.')).toBeInTheDocument();
  },
};
