import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './.storybook/visual-tests',
  outputDir: './test-results',
  snapshotPathTemplate: '{testDir}/__screenshots__/{arg}{ext}',
  fullyParallel: true,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? 'github' : 'list',
  expect: {
    toHaveScreenshot: {
      animations: 'disabled',
      caret: 'hide',
      maxDiffPixelRatio: 0.01,
    },
  },
  use: {
    baseURL: 'http://127.0.0.1:6006',
    browserName: 'chromium',
    viewport: { width: 1280, height: 900 },
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
    colorScheme: 'light',
  },
  webServer: {
    command: 'npm run storybook',
    url: 'http://127.0.0.1:6006',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
