import type { Meta, StoryObj } from '@storybook/react-vite';
import { ShapeAndShadow } from './ShapeAndShadow';

const meta = {
  title: 'Foundations/Radius & elevation',
  component: ShapeAndShadow,
  parameters: {
    docs: {
      description: {
        component: [
          'Corner radii (`--radius-*`) and shadows (`--shadow-*`), declared in `src/styles/_tokens.scss`.',
          'The dark theme re-declares the shadow tokens with heavier opacity; the radius tokens are shared.',
        ].join('\n'),
      },
    },
  },
} satisfies Meta<typeof ShapeAndShadow>;

export default meta;

export const RadiusAndShadows: StoryObj<typeof meta> = {};
