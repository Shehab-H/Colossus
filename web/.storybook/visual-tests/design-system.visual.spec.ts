import { expect, test } from '@playwright/test';

const stories = [
  { id: 'foundations-colors--semantic-colors', snapshot: 'foundations-colors' },
  { id: 'foundations-typography--type-scale', snapshot: 'foundations-typography' },
  { id: 'foundations-spacing--space-scale', snapshot: 'foundations-spacing' },
  { id: 'foundations-radius-elevation--radius-and-shadows', snapshot: 'foundations-radius-elevation' },
  { id: 'foundations-theming--light-and-dark', snapshot: 'foundations-theming' },
  { id: 'components-button--variants', snapshot: 'component-button-variants' },
  { id: 'components-badge--tones', snapshot: 'component-badge-tones' },
  { id: 'components-feedback--alert-tones', snapshot: 'component-alert-tones' },
  { id: 'map-viewer-workspace--workspace', snapshot: 'viewer-workspace' },
] as const;

for (const theme of ['light', 'dark'] as const) {
  for (const story of stories) {
    test(`${story.snapshot} · ${theme}`, async ({ page }) => {
      await page.goto(`/iframe.html?id=${story.id}&viewMode=story&globals=theme:${theme}`);
      await page.locator('#storybook-root').waitFor({ state: 'visible' });
      await page.evaluate(() => document.fonts.ready);
      await expect(page.locator('#storybook-root')).toHaveScreenshot(`${story.snapshot}-${theme}.png`);
    });
  }
}
