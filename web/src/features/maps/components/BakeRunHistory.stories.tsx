import type { Meta, StoryObj } from '@storybook/react-vite';
import { expect, waitFor } from 'storybook/test';
import type { ManagedBakeRun } from '@/types/bake';
import { bakeRun, cancelledRun, countRuns, failedRun, queuedRun, runHistoryRoutes, runningRun, settledRuns } from '../../../../.storybook/fixtures/runs';
import { mockApi, neverResolves } from '../../../../.storybook/support/mockApi';
import BakeRunHistory from './BakeRunHistory';

const MAP_ID = 'map-network-coverage';
const allRuns = settledRuns(60);
const activeRuns = [runningRun, queuedRun, ...settledRuns(8)];
const failedRuns = [failedRun, ...settledRuns(5)];
const succeededRuns = Array.from({ length: 40 }, (_, index) => bakeRun(`run-ok-${index}`));

const meta = {
  title: 'Maps/BakeRunHistory',
  component: BakeRunHistory,
  parameters: {
    layout: 'padded',
    docs: {
      description: {
        component: [
          'The run history of one map. It calls `GET /api/maps/{id}/runs?status=…&cursor=…&limit=25` through the real query options in `api/bakes.ts`; these stories answer that request from fixtures by replacing `fetch`.',
          '',
          '- The filter tabs (All, Active, Failed, Succeeded, Cancelled) show the server totals from the `counts` prop (`runCounts` of the map detail) and send the matching `status` values.',
          '- Queued and running runs show a progress bar and, unless a cancellation was requested, a `Cancel` button. While any run is active the list polls every 1.5 s.',
          '- Failed runs with details have a `Details` button that expands the failure text.',
          '- `Load older runs` appears while the response carries a `nextCursor`.',
        ].join('\n'),
      },
    },
  },
  args: { mapId: MAP_ID, counts: countRuns(allRuns) },
  argTypes: {
    mapId: { control: 'text', description: 'Id of the map whose runs are listed.' },
    counts: { control: 'object', description: 'Run totals per status, from the map detail response.' },
  },
} satisfies Meta<typeof BakeRunHistory>;

export default meta;
type Story = StoryObj<typeof meta>;

const withRuns = (runs: ManagedBakeRun[]): Pick<Story, 'args' | 'beforeEach'> => ({
  args: { counts: countRuns(runs) },
  beforeEach: () => mockApi(runHistoryRoutes(MAP_ID, runs)),
});

export const Paged: Story = {
  ...withRuns(allRuns),
  parameters: { docs: { description: { story: '60 settled runs in pages of 25. `Load older runs` fetches the next page with the cursor from the previous one.' } } },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Showing 25 of 60')).toBeInTheDocument();
    await userEvent.click(canvas.getByRole('button', { name: 'Load older runs' }));
    await expect(await canvas.findByText('Showing 50 of 60')).toBeInTheDocument();
  },
};

export const ActiveRun: Story = {
  ...withRuns(activeRuns),
  parameters: { docs: { description: { story: 'A running and a queued run. Cancelling one posts to the cancel endpoint; the list then refetches and the button disappears.' } } },
  play: async ({ canvas, userEvent }) => {
    const cancelButtons = await canvas.findAllByRole('button', { name: 'Cancel' });
    await expect(cancelButtons).toHaveLength(2);
    await userEvent.click(cancelButtons[0]);
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Cancel' })).toHaveLength(1));
  },
};

export const FailedWithDetails: Story = {
  ...withRuns(failedRuns),
  parameters: { docs: { description: { story: 'Selecting the Failed tab filters on the server; `Details` reveals the failure text.' } } },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Failed/ }));
    const details = (await canvas.findAllByRole('button', { name: 'Details' }))[0];
    await expect(details).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(details);
    await expect(details).toHaveAttribute('aria-expanded', 'true');
    await expect(canvas.getByText(/MEMORY_LIMIT_EXCEEDED/)).toBeInTheDocument();
  },
};

export const OnlySucceeded: Story = {
  ...withRuns(succeededRuns),
  parameters: { docs: { description: { story: 'A long list without any actionable row; the table scrolls inside its own region.' } } },
};

export const CancelledRun: Story = {
  ...withRuns([cancelledRun, ...settledRuns(3)]),
};

export const NoMatchingRuns: Story = {
  ...withRuns(succeededRuns),
  parameters: { docs: { description: { story: 'Runs exist, but none match the selected filter.' } } },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Failed/ }));
    await expect(await canvas.findByText('No runs match this filter.')).toBeInTheDocument();
  },
};

export const NeverQueued: Story = {
  ...withRuns([]),
  parameters: { docs: { description: { story: 'A map that has never been queued.' } } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This map has never been queued.')).toBeInTheDocument();
  },
};

export const Loading: Story = {
  beforeEach: () => mockApi([{ method: 'GET', path: `/maps/${MAP_ID}/runs`, reply: neverResolves }]),
};

export const LoadFailed: Story = {
  beforeEach: () => mockApi([{
    method: 'GET',
    path: `/maps/${MAP_ID}/runs`,
    reply: () => ({ status: 503, body: { error: 'The run history is temporarily unavailable.' } }),
  }]),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('alert')).toHaveTextContent('The run history is temporarily unavailable.');
  },
};
