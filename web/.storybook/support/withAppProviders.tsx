import type { Decorator } from '@storybook/react-vite';
import { AppProviders } from './AppProviders';

export const withAppProviders: Decorator = (Story) => (
  <AppProviders>
    <Story />
  </AppProviders>
);
