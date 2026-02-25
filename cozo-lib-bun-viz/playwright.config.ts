import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  // Use a filename suffix that bun's test runner will not pick up.
  // This avoids `bun test` (repo-wide) accidentally executing Playwright specs.
  testMatch: '**/*.pw.ts',
  timeout: 60_000,
  expect: {
    timeout: 15_000,
  },
  fullyParallel: true,
  retries: process.env.CI ? 2 : 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:4174',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  webServer: [
    {
      name: 'backend',
      cwd: './server',
      command: 'bun run start',
      url: 'http://127.0.0.1:4175/health',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
    {
      name: 'frontend',
      cwd: './frontend',
      command: 'npm run dev -- --host 127.0.0.1 --port 4174 --strictPort',
      url: 'http://127.0.0.1:4174',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: {
        VITE_API_BASE: 'http://127.0.0.1:4175',
      },
    },
  ],

  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
