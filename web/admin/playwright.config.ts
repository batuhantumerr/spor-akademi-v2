import { defineConfig, devices } from '@playwright/test';

import { apiUrl, appPort, appUrl } from './e2e/support/urls';

/**
 * End-to-end tests (P1-36, DEC-240) against a real API and PostgreSQL. Run them through
 * `bash scripts/e2e.sh`, which prepares a throwaway database and exports the E2E_* settings below.
 *
 * - Desktop Chromium, Firefox and WebKit run the School Admin flows (NFR-CMP-01), including the
 *   online dues payment through the Fake sandbox provider (Phase 2 exit criterion, DEC-241).
 * - Phone-sized Chromium and WebKit run the coach attendance flow (NFR-USA-01, NFR-PRF-03).
 * - One worker, no retries: login and refresh are rate limited per client IP (10/min, NFR-SEC-14),
 *   and every browser reaches the API through the same local proxy address (e2e/serve.mjs).
 */
const ci = !!process.env['CI'];
const adminSpecs = /[\\/]admin[\\/][^\\/]+\.e2e\.ts$/;
const coachSpecs = /[\\/]coach[\\/][^\\/]+\.e2e\.ts$/;

const required = [
  'E2E_API_CONNECTION',
  'E2E_JWT_SIGNING_KEY',
  'E2E_PLATFORM_ADMIN_EMAIL',
  'E2E_PLATFORM_ADMIN_PASSWORD',
  'E2E_TLS_KEY',
  'E2E_TLS_CERT',
];
const missing = required.filter((name) => !process.env[name]);
if (missing.length > 0) {
  throw new Error(
    `Missing ${missing.join(', ')}. Run the suite with "bash scripts/e2e.sh" from the repository root.`,
  );
}

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  outputDir: './test-results',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: ci,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  reporter: ci
    ? [['github'], ['list'], ['html', { open: 'never' }]]
    : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: appUrl,
    // The app is served over HTTPS with a per-run self-signed certificate (e2e/serve.mjs).
    ignoreHTTPSErrors: true,
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'chromium',
      testMatch: adminSpecs,
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'firefox',
      testMatch: adminSpecs,
      use: { ...devices['Desktop Firefox'] },
    },
    {
      name: 'webkit',
      testMatch: adminSpecs,
      use: { ...devices['Desktop Safari'] },
    },
    {
      name: 'mobile-chromium',
      testMatch: coachSpecs,
      use: { ...devices['Pixel 5'], viewport: { width: 360, height: 780 } },
    },
    {
      name: 'mobile-webkit',
      testMatch: coachSpecs,
      use: { ...devices['iPhone 13'] },
    },
  ],
  webServer: [
    {
      name: 'api',
      command:
        'dotnet run --project ../../src/SporAkademi.Api -c Release --no-build --no-launch-profile',
      url: `${apiUrl}/health/ready`,
      reuseExistingServer: false,
      timeout: 120_000,
      stdout: 'ignore',
      stderr: 'pipe',
      env: {
        // Staging, not Production: the Fake payment provider (DEC-347) never runs in Production, and the online
        // payment flow (P2-F1) needs it. Staging runs the same code paths otherwise (DEC-241).
        ASPNETCORE_ENVIRONMENT: 'Staging',
        Payments__Provider: 'Fake',
        ASPNETCORE_URLS: apiUrl,
        ConnectionStrings__App: process.env['E2E_API_CONNECTION']!,
        Jwt__SigningKey: process.env['E2E_JWT_SIGNING_KEY']!,
        PlatformAdmin__Email: process.env['E2E_PLATFORM_ADMIN_EMAIL']!,
        PlatformAdmin__InitialPassword: process.env['E2E_PLATFORM_ADMIN_PASSWORD']!,
      },
    },
    {
      name: 'admin',
      // The production bundle that scripts/e2e.sh built (same as the admin image), with /api proxied.
      command: `node e2e/serve.mjs ${appPort} ${apiUrl}`,
      url: appUrl,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 30_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
