import type { Meta, StoryObj } from '@storybook/react-vite';
import { SpacingScale } from './SpacingScale';

const meta = {
  title: 'Foundations/Spacing',
  component: SpacingScale,
  parameters: {
    docs: {
      description: {
        component: [
          'The space scale (`--space-*`), control heights (`--control-height-*`) and layout dimensions, declared in `src/styles/_tokens.scss`.',
          'The bar next to each token is drawn with that token, so its length is the real length.',
        ].join('\n'),
      },
    },
  },
} satisfies Meta<typeof SpacingScale>;

export default meta;

export const SpaceScale: StoryObj<typeof meta> = {};
