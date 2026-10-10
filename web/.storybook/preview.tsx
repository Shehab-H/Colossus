import type { Preview, Renderer } from '@storybook/react-vite';
import { withThemeByDataAttribute } from '@storybook/addon-themes';
import '@fontsource/inter/latin-400.css';
import '@fontsource/inter/latin-500.css';
import '@fontsource/inter/latin-600.css';
import '@fontsource/inter/latin-700.css';
import '@/styles/index.scss';
import '@/features/map-viewer/viewer.scss';
import './workbench.scss';

const preview: Preview = {
  decorators: [
    withThemeByDataAttribute<Renderer>({
      themes: { light: 'light', dark: 'dark' },
      defaultTheme: 'light',
      attributeName: 'data-theme',
    }),
  ],
  parameters: {
    layout: 'fullscreen',
    controls: { expanded: true },
    options: { storySort: { order: ['Foundations', 'Components', 'Map Viewer'] } },
    a11y: { test: 'error' },
  },
  tags: ['autodocs', 'test'],
};

export default preview;
