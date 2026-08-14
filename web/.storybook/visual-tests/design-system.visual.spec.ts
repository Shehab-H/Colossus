import { expect, test } from '@playwright/test';

const stories = [
  { id: 'foundations-design-system--showcase', snapshot: 'design-system' },
  { id: 'widgets-map-viewer--workspace', snapshot: 'viewer-workspace' },
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
