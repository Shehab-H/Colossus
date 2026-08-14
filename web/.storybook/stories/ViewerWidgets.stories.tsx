import { useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect } from 'storybook/test';
import type { ChannelSpec } from '@domain/map/manifest';
import { Badge, Button } from '@shared/ui';
import Controls from '@widgets/map-viewer/Controls';
import InspectPanel from '@widgets/map-viewer/InspectPanel';
import LegendBox from '@widgets/map-viewer/Legend';
import {
  MapWindow,
  PanelSection,
  ViewerHeader,
  ViewerPanel,
  ViewerShell,
} from '@widgets/map-viewer/ViewerShell';

const meta = {
  title: 'Widgets/Map Viewer',
  parameters: { layout: 'fullscreen' },
} satisfies Meta;

export default meta;
type Story = StoryObj<typeof meta>;

const channels: ChannelSpec[] = [
  { name: 'feature_class', column: 'feature_class', role: 'dimension', type: 'string' },
  { name: 'country_code', column: 'country_code', role: 'dimension', type: 'string' },
  { name: 'modification_date', column: 'modification_date', role: 'temporal', type: 'date' },
];

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
          <Controls
            colorChannels={channels}
            colorChannel={colorChannel}
            onColorChannelChange={setColorChannel}
            channels={channels}
            options={{
              feature_class: ['A', 'H', 'L', 'P', 'S'],
              country_code: ['DE', 'EG', 'GB', 'US'],
              modification_date: ['2026-01-01', '2026-08-14'],
            }}
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
          <LegendBox legend={{
            channel: 'signal_strength', note: 'gradient', kind: 'continuous',
            gradient: [[255, 239, 160], [230, 0, 0], [105, 0, 45]], min: -120, midpoint: -80, max: -40,
          }} />
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
