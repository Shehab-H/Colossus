import type { Preview, ReactRenderer } from '@storybook/react-vite';
import { withThemeByDataAttribute } from '@storybook/addon-themes';
import '@fontsource/inter/latin-400.css';
import '@fontsource/inter/latin-500.css';
import '@fontsource/inter/latin-600.css';
import '@fontsource/inter/latin-700.css';
import '@/styles/index.scss';
import '@/app/managementStyles.scss';
import '@/features/sources/components/sourceWorkspace.scss';
import '@/features/sources/components/sourceForm.scss';
import '@/features/map-viewer/viewer.scss';
import './workbench.scss';
import { withAppProviders } from './support/withAppProviders';

const preview: Preview = {
  decorators: [
    withAppProviders,
    withThemeByDataAttribute<ReactRenderer>({
      themes: { light: 'light', dark: 'dark' },
      defaultTheme: 'light',
      attributeName: 'data-theme',
    }),
  ],
  parameters: {
    layout: 'fullscreen',
    controls: { expanded: true },
    options: {
      storySort: {
        order: [
          'Introduction',
          'Architecture',
          'Foundations', ['Colors', 'Typography', 'Spacing', 'Radius & elevation', 'Theming'],
          'Components', ['Button', 'Badge', 'Card', 'Feedback', 'Field', 'Page', 'Dialog', 'RouteLink', 'ThemeToggle', 'Layouts'],
          'Maps', ['MapCatalogTable', 'BakeRunHistory', 'MapConfigurationBuilder', 'ColorEncodingEditor'],
          'Sources', ['ConnectionForm', 'DatasetForm', 'ConnectionList', 'DatasetList', 'PreviewPanel'],
          'Map Viewer', ['Workspace', 'Legend', 'InspectPanel', 'ViewerControls', 'EmbedDisclosure', 'Layout'],
        ],
      },
    },
    a11y: { test: 'error' },
  },
  tags: ['autodocs', 'test'],
};

export default preview;
