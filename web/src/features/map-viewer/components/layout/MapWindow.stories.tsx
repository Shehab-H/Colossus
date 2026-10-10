import type { Meta, StoryObj } from '@storybook/react-vite';
import { MapWindow } from './MapWindow';

const meta = {
  title: 'Map Viewer/Layout/MapWindow',
  component: MapWindow,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: 'The central region of the viewer shell: a labelled section holding the map canvas and, below it, a status bar. The real canvas is deck.gl over MapLibre and needs tiles from a release, so these stories use a static placeholder.',
      },
    },
  },
  decorators: [(Story) => <div className="story-map-frame"><Story /></div>],
  args: {
    map: <div className="workbench-map map-canvas" role="img" aria-label="Map preview"><div className="workbench-map__land" aria-hidden="true" /></div>,
    status: <><span>4 tiles</span><span>128,212 cells resident</span></>,
  },
  argTypes: {
    map: { control: false, description: 'The map canvas element.' },
    status: { control: false, description: 'Content of the status bar.' },
  },
} satisfies Meta<typeof MapWindow>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};
