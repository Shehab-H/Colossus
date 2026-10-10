import type { StorybookConfig } from '@storybook/react-vite';

const config: StorybookConfig = {
  stories: [
    './docs/**/*.mdx',
    './foundations/**/*.stories.@(ts|tsx)',
    '../src/**/*.stories.@(ts|tsx)',
  ],
  addons: [
    '@storybook/addon-docs',
    '@storybook/addon-a11y',
    '@storybook/addon-themes',
    '@storybook/addon-vitest',
  ],
  framework: {
    name: '@storybook/react-vite',
    options: {},
  },
  features: {
    sidebarOnboardingChecklist: false,
    menuOnboardingChecklist: false,
  },
  staticDirs: ['../public'],
};

export default config;
