import fs from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';

const artifactRoot = path.resolve(__dirname, '..', '..', 'artifacts', 'browser-validation', 'route-smoke');
const manifestPath = path.join(artifactRoot, 'manifest.json');

type SmokeRoute = {
  name: string;
  slug: string;
  routePath: string;
  expectedTitle: string;
  assertReady: (page: Page) => Promise<void>;
  interact?: (page: Page) => Promise<void>;
};

type RouteArtifact = {
  route: string;
  path: string;
  screenshot: string;
  capturedAtUtc: string;
};

const routeArtifacts: RouteArtifact[] = [];

const routes: SmokeRoute[] = [
  {
    name: 'Memories',
    slug: 'memories',
    routePath: '/memories',
    expectedTitle: 'Memories - MemorySmith',
    assertReady: async (page) => {
      await expect(page.getByRole('region', { name: 'Memory search' })).toBeVisible();
      await expect(page.getByRole('complementary', { name: 'Memory results' })).toBeVisible();
    },
  },
  {
    name: 'Pages',
    slug: 'pages',
    routePath: '/pages',
    expectedTitle: 'Pages - MemorySmith',
    assertReady: async (page) => {
      await expect(page.getByRole('region', { name: 'Page search' })).toBeVisible();
      await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();
    },
    interact: async (page) => {
      await hidePagesNavigation(page);
      await page.getByRole('button', { name: 'Tree' }).click();
      await expect(page.getByRole('complementary', { name: 'Pages' })).toBeVisible();
    },
  },
  {
    name: 'Chat',
    slug: 'chat',
    routePath: '/chat',
    expectedTitle: 'Chat - MemorySmith',
    assertReady: async (page) => {
      await expect(page.getByRole('region', { name: 'MemorySmith chat' })).toBeVisible();
      await expect(page.getByTitle('Attach files')).toContainText('Attach');
      await expect(page.locator('#chat-file-input')).toHaveAttribute('aria-label', 'Attach files');
    },
  },
  {
    name: 'Tasks',
    slug: 'tasks',
    routePath: '/tasks',
    expectedTitle: 'Tasks - MemorySmith',
    assertReady: async (page) => {
      await expect(page.getByRole('region', { name: 'Tasks workbench' })).toBeVisible();
      await expect(page.getByRole('complementary', { name: 'Task list' })).toBeVisible();
    },
    interact: async (page) => {
      await selectFirstTask(page);
      await collapseTaskList(page);
      await reopenTaskList(page);
    },
  },
  {
    name: 'Health',
    slug: 'health',
    routePath: '/health',
    expectedTitle: 'Health - MemorySmith',
    assertReady: async (page) => {
      await expect(page.getByRole('heading', { name: 'Health & Activity', exact: true })).toBeVisible();
    },
    interact: async (page) => {
      await page.getByRole('button', { name: 'Refresh' }).click();
      await expect(page.getByRole('heading', { name: 'Health & Activity', exact: true })).toBeVisible();
    },
  },
];

test.describe('Route smoke', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeAll(() => {
    fs.rmSync(artifactRoot, { recursive: true, force: true });
    fs.mkdirSync(artifactRoot, { recursive: true });
  });

  test.afterAll(() => {
    fs.writeFileSync(manifestPath, `${JSON.stringify(routeArtifacts, null, 2)}\n`, 'utf8');
  });

  for (const route of routes) {
    test(`${route.name} renders and captures smoke artifact`, async ({ page }, testInfo) => {
      const browserErrors = collectRelevantBrowserErrors(page);

      await page.goto(route.routePath);
      await expect(page).toHaveURL(new RegExp(`${escapeRegExp(route.routePath)}$`));
      await expect(page).toHaveTitle(route.expectedTitle);
      await expect(page.getByRole('button', { name: 'Toggle primary navigation' })).toHaveAttribute('title', 'Toggle primary navigation');

      await route.assertReady(page);

      if (route.interact) {
        await route.interact(page);
      }

      const screenshotFileName = `${route.slug}.png`;
      await page.screenshot({ path: path.join(artifactRoot, screenshotFileName), fullPage: true });
      routeArtifacts.push({
        route: route.name,
        path: route.routePath,
        screenshot: screenshotFileName,
        capturedAtUtc: new Date().toISOString(),
      });

      if (browserErrors.length > 0) {
        await testInfo.attach('browser-errors', {
          body: browserErrors.join('\n'),
          contentType: 'text/plain',
        });
      }

      expect(browserErrors, `Expected no route-smoke browser errors on ${route.routePath}, got: ${browserErrors.join(' | ')}`).toEqual([]);
    });
  }
});

function collectRelevantBrowserErrors(page: Page): string[] {
  const errors: string[] = [];

  page.on('pageerror', (error) => {
    errors.push(`pageerror: ${error.message}`);
  });

  page.on('console', (message) => {
    if (message.type() !== 'error') {
      return;
    }

    const text = message.text();
    if (
      text.includes('error applying batch') ||
      text.includes('Unhandled exception on the current circuit') ||
      text.includes('Unhandled exception rendering component') ||
      text.includes('Failed to rejoin')
    ) {
      errors.push(`console: ${text}`);
    }
  });

  return errors;
}

async function hidePagesNavigation(page: Page): Promise<void> {
  await expect(async () => {
    const pagesPanel = page.getByRole('complementary', { name: 'Pages' });
    if ((await pagesPanel.count()) === 0) {
      return;
    }

    await page.getByRole('button', { name: 'Toggle page navigation' }).click();
    await expect(pagesPanel).toHaveCount(0, { timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

async function collapseTaskList(page: Page): Promise<void> {
  await expect(async () => {
    const taskList = page.getByRole('complementary', { name: 'Task list' });
    if ((await taskList.count()) === 0) {
      return;
    }

    await page.getByRole('button', { name: 'Toggle task list' }).click();
    await expect(taskList).toHaveCount(0, { timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

async function reopenTaskList(page: Page): Promise<void> {
  await expect(async () => {
    const taskList = page.getByRole('complementary', { name: 'Task list' });
    if ((await taskList.count()) > 0) {
      return;
    }

    await page.getByRole('button', { name: 'Toggle task list' }).click();
    await expect(taskList).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

async function selectFirstTask(page: Page): Promise<void> {
  const firstTask = page.locator('.tasks-list-pane .proposal-row').first();
  await expect(firstTask).toBeVisible();
  await firstTask.click();
  await expect(page.getByRole('main', { name: 'Task detail' })).toBeVisible();
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}