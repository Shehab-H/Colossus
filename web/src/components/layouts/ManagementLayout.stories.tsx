import type { Meta, StoryObj } from '@storybook/react-vite';
import { Button, EmptyState, PageHeader } from '@/components/ui';
import { ButtonRouteLink } from '@/components/ui/RouteLink';
import ManagementLayout from './ManagementLayout';

const THEME_STORAGE_KEY = 'colossus-theme';

const meta = {
  title: 'Components/Layouts/ManagementLayout',
  component: ManagementLayout,
  parameters: {
    docs: {
      description: {
        component: [
          'The page frame of every management route: a skip link, a brand header linking to `/maps`, an `actions` slot and the theme toggle, then the page content.',
          '',
          'The layout owns the theme through `useTheme`, which writes `data-theme` on `<html>`. These stories seed the saved theme from the toolbar when they load; pressing the layout\'s own toggle afterwards changes the theme for the whole preview.',
        ].join('\n'),
      },
    },
  },
  beforeEach: ({ globals }) => {
    localStorage.setItem(THEME_STORAGE_KEY, String(globals.theme ?? 'light'));
    return () => localStorage.removeItem(THEME_STORAGE_KEY);
  },
  argTypes: {
    actions: { control: false, description: 'Controls placed in the header, left of the theme toggle.' },
    children: { control: false, description: 'Page content.' },
  },
} satisfies Meta<typeof ManagementLayout>;

export default meta;
type Story = StoryObj<typeof meta>;

export const CatalogPage: Story = {
  args: {
    actions: (
      <>
        <ButtonRouteLink variant="ghost" to="/sources">Sources</ButtonRouteLink>
        <ButtonRouteLink variant="primary" to="/maps/new">Create map</ButtonRouteLink>
      </>
    ),
    children: (
      <>
        <PageHeader
          eyebrow="Control plane"
          title="Maps"
          description="Manage map definitions, bake runs, releases, schedules, and archived records."
        />
        <EmptyState
          eyebrow="No maps yet"
          title="Create the first map"
          description="Choose a migrated dataset, configure its view, then publish it when the bake workflow is connected."
          action={<Button variant="primary">Create map</Button>}
        />
      </>
    ),
  },
};

export const WithoutActions: Story = {
  args: {
    children: <PageHeader eyebrow="Map overview" title="Network coverage" description="Without an actions slot only the theme toggle is shown." />,
  },
};
