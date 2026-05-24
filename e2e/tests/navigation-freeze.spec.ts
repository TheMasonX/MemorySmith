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

  test('pages slug navigation remains stable and avoids circuit termination', async ({ page }) => {
    const pageErrors: string[] = [];

    page.on('pageerror', (error) => {
      pageErrors.push(error.message);
    });

    page.on('console', (message) => {
      if (message.type() !== 'error') {
        return;
      }

      const text = message.text();
      if (
        text.includes('error applying batch') ||
        text.includes('Unhandled exception on the current circuit') ||
        text.includes('Unhandled exception rendering component')
      ) {
        pageErrors.push(text);
      }
    });

    await page.goto('/pages/features/chat-and-agent');
    await expect(page).toHaveURL(/\/pages\/features\/chat-and-agent$/);
    await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();

    const nav = appNavigation(page);
    await expect(nav.getByRole('link', { name: /^Pages$/ })).toBeVisible();
    await expect(nav.getByRole('link', { name: /^Memories$/ })).toBeVisible();

    const slugPathHops = [
      '/pages/features/health-and-diagnostics',
      '/pages/features/api-and-mcp',
      '/pages/features/chat-and-agent',
      '/pages/features/route-map',
      '/pages/features/health-and-diagnostics',
    ];

    for (const path of slugPathHops) {
      await page.goto(path);
      await expect(page).toHaveURL(new RegExp(`${path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
      await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();
    }

    const newButton = page.getByRole('button', { name: 'New' });
    if (await newButton.isVisible()) {
      const stamp = Date.now().toString();
      const slug = `e2e/pages-lock-regression-${stamp}`;
      const title = `Pages Lock Regression ${stamp}`;

      await newButton.click();
      await page.getByLabel('Slug').fill(slug);
      await page.getByLabel('Title').fill(title);
      await page.getByLabel('Markdown editor').fill(`# ${title}\n\nNavigation regression probe.`);
      await page.getByRole('button', { name: 'Save' }).click();

      await expect(page).toHaveURL(new RegExp(`/pages/${slug.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
      await page.goto('/memories');
      await expect(page).toHaveURL(/\/memories$/);
      await page.goto(`/pages/${slug}`);
      await expect(page).toHaveURL(new RegExp(`/pages/${slug.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`));
    }

    await navigateAndAssert(page, 'Tasks', /\/tasks$/, 'Tasks');
    await navigateAndAssert(page, 'Pages', /\/pages(\/.*)?$/, 'Pages');

    expect(pageErrors, `Expected no page-lock circuit failures, got: ${pageErrors.join(' | ')}`).toEqual([]);
  });

  test('pages tree and flat item clicks do not terminate the circuit', async ({ page }) => {
    const pageErrors: string[] = [];

    page.on('pageerror', (error) => {
      pageErrors.push(error.message);
    });

    page.on('console', (message) => {
      if (message.type() !== 'error') {
        return;
      }

      const text = message.text();
      if (
        text.includes('error applying batch') ||
        text.includes('Unhandled exception on the current circuit') ||
        text.includes('Unhandled exception rendering component')
      ) {
        pageErrors.push(text);
      }
    });

    await page.goto('/pages');
    await expect(page).toHaveURL(/\/pages(\/.*)?$/);
    await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Tree' }).click();
    const treeItems = page.getByRole('button', { name: /^Open / });
    await expect(treeItems.first()).toBeVisible();
    const treeCount = await treeItems.count();
    for (let i = 0; i < Math.min(treeCount, 12); i++) {
      await treeItems.nth(i).click();
      await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();
    }

    await page.getByRole('button', { name: 'Flat' }).click();
    const pagesPanel = page.getByRole('complementary', { name: 'Pages' });
    const flatTitles = pagesPanel.locator('.wiki-result-title');
    await expect(flatTitles.first()).toBeVisible();
    const flatCount = await flatTitles.count();
    for (let i = 0; i < Math.min(flatCount, 12); i++) {
      // Resolve the target fresh each iteration to avoid detached-node flakiness while the list rerenders.
      const titleText = ((await flatTitles.nth(i).innerText()) ?? '').trim();
      await expect(titleText.length).toBeGreaterThan(0);
      await pagesPanel.locator('.wiki-result-title', { hasText: titleText }).first().click();
      await expect(page.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();
    }

    expect(pageErrors, `Expected no tree/flat click circuit failures, got: ${pageErrors.join(' | ')}`).toEqual([]);
  });
});

async function navigateAndAssert(
  page: import('@playwright/test').Page,
  navLabel: string,
  urlPattern: RegExp,
  expectedHeadingOrRegion: string,
  byRegionLabel = false,
): Promise<void> {
  const nav = appNavigation(page);
  const escapedNavLabel = navLabel.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await nav.getByRole('link', { name: new RegExp(`^${escapedNavLabel}$`) }).click();
  await expect(page).toHaveURL(urlPattern);

  if (byRegionLabel) {
    await expect(page.getByRole('region', { name: expectedHeadingOrRegion })).toBeVisible();
    return;
  }

  await expect(page.getByRole('heading', { name: expectedHeadingOrRegion, exact: true }).first()).toBeVisible();
}

function appNavigation(page: import('@playwright/test').Page) {
  return page.getByRole('navigation').first();
}
