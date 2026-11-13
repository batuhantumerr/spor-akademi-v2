import { expect, takeCspViolations, test } from '../support/test';
import { parentUrl } from '../support/urls';

/**
 * P4-01 (DEC-502): both apps are served under the images' Content-Security-Policy (e2e/serve.mjs reads the nginx
 * include files), and a violation is caught (e2e/support/test.ts). Without this, the guard in every other spec could
 * pass because no policy was sent at all.
 */
test('both apps are served under the production policy, and a violation is caught', async ({
  page,
}) => {
  for (const [url, styles] of [
    ['/', "style-src 'self' 'unsafe-inline'"],
    [`${parentUrl}/`, "style-src 'self';"],
  ] as const) {
    const response = await page.goto(url);
    const headers = response?.headers() ?? {};
    const policy = headers['content-security-policy'] ?? '';
    expect(policy).toContain("default-src 'self'");
    expect(policy).toContain("script-src 'self';");
    expect(policy).toContain(styles);
    expect(policy).toContain("object-src 'none'");
    expect(policy).toContain("frame-ancestors 'none'");
    expect(headers['permissions-policy']).toContain('camera=()');
    expect(headers['x-content-type-options']).toBe('nosniff');

    // Positive control: an injected inline script does not run, and the guard sees the violation.
    const ran = await page.evaluate(() => {
      const script = document.createElement('script');
      script.textContent = 'document.documentElement.dataset["inlineRan"] = "yes";';
      document.head.appendChild(script);
      return document.documentElement.dataset['inlineRan'] === 'yes';
    });
    expect(ran).toBe(false);
    await expect
      .poll(() =>
        takeCspViolations().some((violation) => violation.includes('[csp-violation] script-src')),
      )
      .toBe(true);
  }
});
