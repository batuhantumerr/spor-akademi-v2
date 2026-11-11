import { defineConfig, devices } from '@playwright/test';

import { apiUrl, appPort, appUrl, parentPort, parentUrl } from './e2e/support/urls';

/**
 * End-to-end tests (P1-36, DEC-240) against a real API and PostgreSQL. Run them through
 * `bash scripts/e2e.sh`, which prepares a throwaway database and exports the E2E_* settings below.
 *
 * - Desktop Chromium, Firefox and WebKit run the School Admin flows (NFR-CMP-01), including the
 *   online dues payment through the Fake sandbox provider (Phase 2 exit criterion, DEC-241).
 * - Phone-sized Chromium and WebKit run the coach attendance flow (NFR-USA-01, NFR-PRF-03).
 * - A phone-sized Chromium runs the parent flow in the parent app (P3-05, P3-06): invitation e-mail, password,
 *   sign-in, child, attendance, dues and online payment. E-mail goes to the run's Mailpit (scripts/e2e.sh).
 * - One worker, no retries: login and refresh are rate limited per client IP (10/min, NFR-SEC-14),
 *   and every browser reaches the API through the same local proxy address (e2e/serve.mjs).
 */
const ci = !!process.env['CI'];
const adminSpecs = /[\\/]admin[\\/][^\\/]+\.e2e\.ts$/;
const coachSpecs = /[\\/]coach[\\/][^\\/]+\.e2e\.ts$/;
const parentSpecs = /[\\/]parent[\\/][^\\/]+\.e2e\.ts$/;

const required = [
  'E2E_API_CONNECTION',
  'E2E_JWT_SIGNING_KEY',
  'E2E_PLATFORM_ADMIN_EMAIL',
  'E2E_PLATFORM_ADMIN_PASSWORD',
  'E2E_TLS_KEY',
  'E2E_TLS_CERT',
  'E2E_SMTP_PORT',
  'E2E_MAILPIT_URL',
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
    {
      name: 'parent-mobile-chromium',
      testMatch: parentSpecs,
      use: {
        ...devices['Pixel 5'],
        viewport: { width: 360, height: 780 },
        baseURL: parentUrl,
        // P3-07: Chromium refuses to register a service worker from a self-signed certificate even with
        // ignoreHTTPSErrors; the browser-wide flag lets the parent app's worker run against the per-run certificate.
        launchOptions: { args: ['--ignore-certificate-errors'] },
      },
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
        // P3-05: e-mail (the parent invitation) goes to the run's Mailpit catcher, plain SMTP on loopback;
        // links point at the e2e app servers.
        Email__Smtp__Host: '127.0.0.1',
        Email__Smtp__Port: process.env['E2E_SMTP_PORT']!,
        Email__Smtp__Security: 'None',
        Email__AppBaseUrl: `${appUrl}/`,
        Email__ParentAppBaseUrl: `${parentUrl}/`,
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
    {
      name: 'parent',
      // The parent app's production build (scripts/e2e.sh builds it, same as the parent image).
      command: `node e2e/serve.mjs ${parentPort} ${apiUrl} ../parent/dist`,
      url: parentUrl,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 30_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
