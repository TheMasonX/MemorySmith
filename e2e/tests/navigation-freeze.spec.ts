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
    await expect(page.getByRole('region', { name: 'Page search' })).toBeVisible();

    await navigateAndAssert(page, 'Memories', /\/memories$/, 'Memory search', true);
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
    const pageSearch = page.getByRole('region', { name: 'Page search' });
    await expect(pageSearch.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();

    await page.getByRole('button', { name: 'Tree' }).click();
    const treeItems = page.getByRole('button', { name: /^Open / });
    await expect(treeItems.first()).toBeVisible();
    const treeCount = await treeItems.count();
    for (let i = 0; i < Math.min(treeCount, 12); i++) {
      await treeItems.nth(i).click();
      await expect(pageSearch.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();
    }

    await page.getByRole('button', { name: 'Flat' }).click();
    const pagesPanel = page.getByRole('complementary', { name: 'Pages' });
    const flatTitles = pagesPanel.locator('.wiki-result-title');
    await expect(flatTitles.first()).toBeVisible();
    const flatCount = await flatTitles.count();
    for (let i = 0; i < Math.min(flatCount, 12); i++) {
      const titleText = ((await flatTitles.nth(i).innerText()) ?? '').trim();
      await expect(titleText.length).toBeGreaterThan(0);
      await pagesPanel.locator('.wiki-result-title', { hasText: titleText }).first().click();
      await expect(pageSearch.getByRole('heading', { name: 'Pages', exact: true })).toBeVisible();
    }

    expect(pageErrors, `Expected no tree/flat click circuit failures, got: ${pageErrors.join(' | ')}`).toEqual([]);
  });

  test('pages navigation controls reopen the sidebar and keep reset hidden', async ({ page }) => {
    await page.setViewportSize({ width: 582, height: 462 });
    await page.goto('/pages/features/chat-and-agent');
    await expect(page).toHaveURL(/\/pages\/features\/chat-and-agent$/);
    await expect(page.getByRole('region', { name: 'Page search' })).toBeVisible();
    await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Reset page view state' })).toHaveCount(0);
    await expectPagesCommandbarLayout(page);

    await hidePagesNavigation(page);
    await page.getByRole('button', { name: 'Flat' }).click();
    await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();

    await hidePagesNavigation(page);
    await page.getByRole('button', { name: 'Tree' }).click();
    await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();

    await hidePagesNavigation(page);
    await page.getByRole('button', { name: 'ToC' }).click();
    await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();
  });

  test('missing pages slug shows persistent recovery state', async ({ page }) => {
    const missingSlug = `e2e/missing-page-${Date.now()}`;

    await page.goto(`/pages/${missingSlug}`);
    await expect(page).toHaveURL(new RegExp(`/pages/${missingSlug}$`));
    await expect(page.getByRole('main', { name: 'Selected page' }).getByText('Page not found')).toBeVisible();
    await expect(page.getByText(missingSlug)).toBeVisible();
    await expect(page.getByRole('link', { name: 'Open Pages root' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Open first page' })).toBeVisible();
  });
});

async function hidePagesNavigation(page: import('@playwright/test').Page): Promise<void> {
  await expect(async () => {
    const pagesPanel = page.getByRole('complementary', { name: 'Pages' });
    if ((await pagesPanel.count()) === 0) {
      return;
    }

    await page.getByRole('button', { name: 'Toggle page navigation' }).click();
    await expect(pagesPanel).toHaveCount(0, { timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

async function expectPagesCommandbarLayout(page: import('@playwright/test').Page): Promise<void> {
  const layout = await page.evaluate(() => {
    const strip = document.querySelector('.pages-search-strip')?.getBoundingClientRect();
    const searchBox = document.querySelector('.pages-search-main .wiki-search-box')?.getBoundingClientRect();
    const clearButton = document.querySelector('.pages-search-clear')?.getBoundingClientRect();
    const searchButton = document.querySelector('.pages-search-submit') as HTMLElement | null;
    const searchButtonRect = searchButton?.getBoundingClientRect();
    const navControls = document.querySelector('.pages-navigation-controls')?.getBoundingClientRect();
    const modeToggle = document.querySelector('.pages-navigation-controls .wiki-mode-toggle')?.getBoundingClientRect();
    const navToggle = document.querySelector('.pages-navigation-controls [aria-label="Toggle page navigation"]')?.getBoundingClientRect();
    const searchIcon = searchButton?.querySelector('.mud-icon-root');
    const searchButtonStyle = searchButton ? getComputedStyle(searchButton) : null;
    const searchIconStyle = searchIcon ? getComputedStyle(searchIcon) : null;

    return {
      clearSearchGap: clearButton && searchButtonRect ? searchButtonRect.left - clearButton.right : Number.POSITIVE_INFINITY,
      clearSearchCenterDelta: clearButton && searchButtonRect ? Math.abs((clearButton.top + clearButton.height / 2) - (searchButtonRect.top + searchButtonRect.height / 2)) : Number.POSITIVE_INFINITY,
      inputSearchGap: searchBox && searchButtonRect ? searchButtonRect.left - searchBox.right : Number.POSITIVE_INFINITY,
      modeNavGap: modeToggle && navToggle ? navToggle.left - modeToggle.right : Number.POSITIVE_INFINITY,
      modeNavCenterDelta: modeToggle && navToggle ? Math.abs((modeToggle.top + modeToggle.height / 2) - (navToggle.top + navToggle.height / 2)) : Number.POSITIVE_INFINITY,
      navRightGap: strip && navControls ? strip.right - navControls.right : Number.POSITIVE_INFINITY,
      searchBackground: searchButtonStyle?.backgroundColor ?? '',
      searchIconColor: searchIconStyle?.color ?? '',
    };
  });

  expect(layout.clearSearchGap, 'Clear should sit immediately beside the filled Search button').toBeLessThanOrEqual(8);
  expect(layout.clearSearchCenterDelta, 'Clear and Search should align vertically').toBeLessThanOrEqual(6);
  // inputSearchGap = gap + clearButton.width + gap (6+30+6=42 when button is 30px).
  // Allow up to 50px to accommodate sub-pixel rounding and MudBlazor sizing variance.
  expect(layout.inputSearchGap, 'Search controls should stay attached to the search input').toBeLessThanOrEqual(50);
  expect(layout.modeNavGap, 'Tree/Flat/ToC should sit directly beside the sidebar toggle').toBeLessThanOrEqual(8);
  expect(layout.modeNavCenterDelta, 'Tree/Flat/ToC and sidebar toggle should align vertically').toBeLessThanOrEqual(6);
  expect(layout.navRightGap, 'Navigation controls should be right aligned in the Pages commandbar').toBeLessThanOrEqual(8);
  expect(layout.searchBackground, 'Search button should have a filled background').not.toBe('rgba(0, 0, 0, 0)');
  expect(layout.searchIconColor, 'Search button icon should be white').toBe('rgb(255, 255, 255)');
}

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
  return page
    .getByRole('complementary')
    .filter({ has: page.getByRole('heading', { name: 'Navigation', exact: true }) })
    .first();
}
