import type { Meta, StoryObj } from '@storybook/react-vite';
import { TypeSpecimens } from './TypeSpecimens';

const meta = {
  title: 'Foundations/Typography',
  component: TypeSpecimens,
  parameters: {
    docs: {
      description: {
        component: [
          'Font families, the type scale, weights and line heights, declared in `src/styles/_tokens.scss`.',
          'Specimens are styled with `var(--font-*)` and `var(--line-height-*)`, so they always match the product.',
          '',
          'The Inter typeface is bundled through `@fontsource/inter` (weights 400, 500, 600 and 700).',
        ].join('\n'),
      },
    },
  },
} satisfies Meta<typeof TypeSpecimens>;

export default meta;

export const TypeScale: StoryObj<typeof meta> = {};
