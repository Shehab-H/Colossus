import type { Meta, StoryObj } from '@storybook/react-vite';
import { ColorPalette } from './ColorPalette';

const meta = {
  title: 'Foundations/Colors',
  component: ColorPalette,
  parameters: {
    docs: {
      description: {
        component: [
          'Semantic color tokens, declared as `--color-*` custom properties in `src/styles/_tokens.scss`.',
          'The tokens are read from the loaded stylesheets at runtime, so this page lists exactly what ships.',
          'Each swatch shows the value resolved for the active theme; switch the theme in the toolbar to compare.',
          '',
          'Components reference these tokens, for example `color: var(--color-text-primary)`. The exceptions are the fixed dark backgrounds behind the map canvas (`MapWindow.scss` and `MapDetailPage.scss`) and colors computed from data, such as legends and the color encoding editor.',
        ].join('\n'),
      },
    },
  },
} satisfies Meta<typeof ColorPalette>;

export default meta;

export const SemanticColors: StoryObj<typeof meta> = {};
