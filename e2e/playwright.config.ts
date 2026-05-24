import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.MEMORYSMITH_BASE_URL ?? 'http://localhost:5089';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  retries: process.env.CI ? 2 : 0,
  timeout: 90_000,
  expect: {
    timeout: 10_000,
  },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  webServer: {
    command: 'dotnet run --project ../MemorySmith.App --launch-profile http',
    url: baseURL,
    timeout: 120_000,
    reuseExistingServer: true,
    env: {
      ASPNETCORE_DETAILEDERRORS: 'true',
    },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
