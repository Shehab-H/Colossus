import type { Meta, StoryObj } from '@storybook/react-vite';
import { ThemeComparison } from './ThemeComparison';

const meta = {
  title: 'Foundations/Theming',
  component: ThemeComparison,
  parameters: {
    docs: {
      description: {
        component: [
          'Both themes are CSS custom properties. `:root` declares the light values; `:root[data-theme="dark"]` re-declares every `--color-*` token (the `emit-theme($dark-theme)` mixin in `src/styles/_tokens.scss`) and the shadow tokens. Some of the re-declared colors are identical to the light ones, so the tables below compare resolved values.',
          'Component stylesheets reference tokens and never select on `data-theme`.',
          '',
          'In the app, `useTheme` (`src/hooks/useTheme.ts`) picks the theme in this order: a `?theme=` query parameter, the choice saved under `colossus-theme` in local storage, the operating system preference, then light.',
          'It writes the result to `data-theme` on the `<html>` element. Storybook sets the same attribute from the theme selector in the toolbar.',
          '',
          'The tables below are built from the stylesheet rules themselves, so a token declared in either block appears here automatically.',
        ].join('\n'),
      },
    },
  },
} satisfies Meta<typeof ThemeComparison>;

export default meta;

export const LightAndDark: StoryObj<typeof meta> = {};
