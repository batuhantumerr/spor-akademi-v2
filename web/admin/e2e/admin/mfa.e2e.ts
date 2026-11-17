import { expect, test } from '../support/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { totpCode } from '../support/totp';
import { answerRefreshWithoutSession } from '../support/ui';

/**
 * P5-01: the Platform Admin signs in in two steps. Global setup enrolled the run's Platform Admin through the API (the
 * first login of a Platform Admin is an enrolment, and it computes the code from the setup key like an authenticator
 * app); here the sign-in form asks for the code after the password and the same key gives the answer.
 *
 * One desktop browser is enough, and it must be one: the server refuses a time step it has already accepted, so a
 * second browser project could not use the same code, and the unit tests cover the rest.
 */
test('a platform admin signs in with a password and a code computed from the setup key', async ({
  page,
  context,
  browserName,
}, testInfo) => {
  test.skip(
    browserName !== 'chromium',
    'One browser only: a code is good once (replay protection).',
  );
  const email = process.env['E2E_PLATFORM_ADMIN_EMAIL']!;
  const password = process.env['E2E_PLATFORM_ADMIN_NEW_PASSWORD']!;
  const secret = process.env['E2E_PLATFORM_ADMIN_MFA_SECRET']!;
  await answerRefreshWithoutSession(context);

  await page.goto('/login');
  await page.getByLabel('E-posta', { exact: true }).fill(email);
  await page.getByLabel('Şifre', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();

  // The password alone gives no session: the page asks for the code.
  await expect(page.getByRole('heading', { level: 1, name: 'İki adımlı doğrulama' })).toBeVisible();
  await expect(page.getByLabel('Şifre', { exact: true })).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'mfa-code-step');

  // A wrong code is refused and the page stays on the second step.
  await page.getByLabel('Doğrulama kodu').fill('000000');
  await page.getByRole('button', { name: 'Doğrula' }).click();
  await expect(page.getByRole('alert')).toContainText('Doğrulama kodu hatalı.');
  await expect(page.getByLabel('Doğrulama kodu')).toBeVisible();

  // The next time step: the server accepts one either side of its own, and the setup's own code is spent.
  await page.getByLabel('Doğrulama kodu').fill(totpCode(secret, 1));
  await page.getByRole('button', { name: 'Doğrula' }).click();
  await expect(page).toHaveURL(/\/platform\/schools$/);

  // Two-step verification is mandatory for this role: the security page says so and offers no way to turn it off.
  await page.getByRole('link', { name: 'Güvenlik' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'İki adımlı doğrulama' })).toBeVisible();
  await expect(page.getByText('Açık', { exact: true })).toBeVisible();
  await expect(page.getByText('Platform yöneticisi hesapları için zorunlu')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Kapat' })).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'security-page');
});
