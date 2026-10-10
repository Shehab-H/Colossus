import path from 'node:path';
import { fileURLToPath, URL } from 'node:url';
import { storybookTest } from '@storybook/addon-vitest/vitest-plugin';
import { playwright } from '@vitest/browser-playwright';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

const directory = path.dirname(fileURLToPath(import.meta.url));

async function storybookProject(theme: 'light' | 'dark') {
  return {
    extends: true as const,
    plugins: await storybookTest({
      configDir: path.join(directory, '.storybook'),
      initialGlobals: { theme },
    }),
    test: {
      name: `storybook-${theme}`,
      browser: {
        enabled: true,
        provider: playwright({}),
        headless: true,
        instances: [{ browser: 'chromium' as const }],
      },
    },
  };
}

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  // These Testing Library dependencies are CommonJS-only. Force Vite to
  // pre-bundle them before Storybook's browser tests import the test runtime.
  optimizeDeps: {
    include: ['aria-query', 'lz-string', 'pretty-format', 'storybook/test'],
  },
  test: {
    projects: [
      {
        extends: true,
        test: {
          name: 'unit',
          include: ['src/**/*.test.ts'],
          environment: 'node',
        },
      },
      await storybookProject('light'),
      await storybookProject('dark'),
    ],
  },
});
