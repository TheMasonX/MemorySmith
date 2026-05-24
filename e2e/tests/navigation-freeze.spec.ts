import { expect, test } from '@playwright/test';

test.describe('Navigation freeze regression', () => {
  test('route hopping keeps content updating across workbench pages', async ({ page }) => {
    const pageErrors: string[] = [];

    page.on('pageerror', (error) => {
      pageErrors.push(error.message);
    });

    page.on('console', (message) => {
      if (message.type() !== 'error') {
        return;
      }

      const text = message.text();
      if (text.includes('error applying batch') || text.includes('Unhandled exception rendering component')) {
        pageErrors.push(text);
      }
    });

    await page.goto('/pages');

    await expect(page).toHaveURL(/\/pages$/);
    await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();

    await navigateAndAssert(page, 'Memories', /\/memories$/, 'Memories');
    await navigateAndAssert(page, 'Chat', /\/chat$/, 'MemorySmith chat', true);
    await navigateAndAssert(page, 'Tasks', /\/tasks$/, 'Tasks');
    await navigateAndAssert(page, 'Pages', /\/pages$/, 'Pages');

    expect(pageErrors, `Expected no client-side render/circuit errors, got: ${pageErrors.join(' | ')}`).toEqual([]);
  });
});

async function navigateAndAssert(
  page: import('@playwright/test').Page,
  navLabel: string,
  urlPattern: RegExp,
  expectedHeadingOrRegion: string,
  byRegionLabel = false,
): Promise<void> {
  await page.getByRole('link', { name: navLabel }).click();
  await expect(page).toHaveURL(urlPattern);

  if (byRegionLabel) {
    await expect(page.getByRole('region', { name: expectedHeadingOrRegion })).toBeVisible();
    return;
  }

  await expect(page.getByRole('heading', { name: expectedHeadingOrRegion, exact: true }).first()).toBeVisible();
}
