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
 *   The phase 3 exit spec (P3-R1) also opens a desktop admin context next to the parent's page.
 * - One worker, no retries: login and refresh are rate limited per client IP (10/min, NFR-SEC-14),
 *   and every browser reaches the API through the same local proxy address (e2e/serve.mjs).
 */
const ci = !!process.env['CI'];
const adminSpecs = /[\\/]admin[\\/][^\\/]+\.e2e\.ts$/;
const coachSpecs = /[\\/]coach[\\/][^\\/]+\.e2e\.ts$/;
const parentSpecs = /[\\/]parent[\\/][^\\/]+\.e2e\.ts$/;
const a11yAdminSpecs = /[\\/]a11y[\\/]admin-[^\\/]+\.e2e\.ts$/;
const a11yParentSpecs = /[\\/]a11y[\\/]parent-[^\\/]+\.e2e\.ts$/;

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

/**
 * P4-11 (DEC-591): which projects run. "flows" leaves out the WCAG audit projects (the CI `e2e` job), "a11y" runs only
 * them (the CI `a11y` job), "all" (the default, `bash scripts/e2e.sh`) runs everything. Selecting by suite instead of
 * listing project names keeps a newly added flow project in the `e2e` job without touching the workflow.
 */
const suite = process.env['E2E_SUITE'] ?? 'all';
if (!['all', 'flows', 'a11y'].includes(suite)) {
  throw new Error(`E2E_SUITE must be all, flows or a11y (got "${suite}").`);
}
const inSuite = (project: { name?: string }): boolean =>
  suite === 'all' || (suite === 'a11y') === !!project.name?.startsWith('a11y');

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
    // P3-11 (DEC-436): the WCAG 2.2 AA audit. Desktop Chromium; each screen is also scanned at 360 px and 320 px by
    // resizing the same page, in both languages and themes (e2e/support/wcag.ts).
    {
      name: 'a11y',
      testMatch: a11yAdminSpecs,
      use: { ...devices['Desktop Chrome'], actionTimeout: 15_000 },
    },
    {
      name: 'a11y-parent',
      testMatch: a11yParentSpecs,
      use: {
        ...devices['Desktop Chrome'],
        actionTimeout: 15_000,
        baseURL: parentUrl,
        launchOptions: { args: ['--ignore-certificate-errors'] },
      },
    },
  ].filter(inSuite),
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
        // P4-02 (DEC-505): every browser of the suite reaches the API from one address and as few users, much faster
        // than people do (the WCAG audit loads hundreds of screens a minute). The general limits are raised; the
        // per-endpoint limits (sign-in, refresh, the parent API) stay as in production.
        RateLimiting__UserPermitsPerMinute: '100000',
        RateLimiting__SchoolWritePermitsPerMinute: '100000',
        RateLimiting__AnonymousPermitsPerMinute: '100000',
      },
    },
    {
      name: 'admin',
      // The production bundle that scripts/e2e.sh built (same as the admin image), with /api proxied.
      // P4-01: with the image's security headers (Content-Security-Policy), read from its nginx include file.
      command: `node e2e/serve.mjs ${appPort} ${apiUrl} dist/admin/browser ../../docker/admin/security-headers.inc.template`,
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
      command: `node e2e/serve.mjs ${parentPort} ${apiUrl} ../parent/dist ../../docker/parent/security-headers.inc`,
      url: parentUrl,
      ignoreHTTPSErrors: true,
      reuseExistingServer: false,
      timeout: 30_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
