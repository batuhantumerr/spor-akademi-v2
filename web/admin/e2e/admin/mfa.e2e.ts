import { BrowserContext, Locator, Page, expect, test } from '../support/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { ApiClient } from '../support/api';
import { emailDomain, newPassword, runId, schoolTimeZone } from '../support/data';
import { TotpSequence, totpCode } from '../support/totp';
import { answerRefreshWithoutSession, dialog, signIn, toast } from '../support/ui';

/**
 * Two-step verification (P5-01, V1-03) in one serial session. Global setup enrolled the run's Platform Admin through the
 * API (the first login of a Platform Admin is an enrolment, and it computes the code from the setup key like an
 * authenticator app); the same key answers the sign-in form here.
 *
 * 1. The Platform Admin signs in with a password and a code (P5-01) and stays signed in for the rest of the file: a code
 *    is good once and never older than the last, so a second sign-in of the same account would have to wait for the
 *    clock. One desktop browser is enough, and it must be one, for the same reason.
 * 2. A School Admin of a school made for this file turns two-step verification on, signs in with a code, then with a
 *    recovery code, and replaces the recovery codes (V1-03, DEC-687): the old ones stop working, the new ones work.
 * 3. The Platform Admin resets that School Admin's two-step verification from the school list; the School Admin signs in
 *    with the password alone again (a first visit to the sign-in page shows no notice). The Platform Admins page lists
 *    the account but offers no reset of one's own.
 * 4. A session that ran out shows the "session expired" notice after a reload, once (V1-03, DEC-690). The refresh cookie
 *    is dropped the way the browser drops it at the absolute limit: the API's shortest idle time is five minutes, too
 *    long for this suite, and answers the same `Auth.SessionExpired` either way (the server-side limits are covered by
 *    SessionLifetimeTests).
 *
 * Sign-in is limited to 10 a minute per client address (NFR-SEC-14) and the specs that follow share it, so this file
 * signs in as few times as it can: the Platform Admin once, the School Admin five times.
 */
test.describe.configure({ mode: 'serial' });
test.skip(
  ({ browserName }) => browserName !== 'chromium',
  'One browser only: a code is good once (replay protection).',
);

let platformContext: BrowserContext;
let platformPage: Page;
let platformToken: string;
/** The School Admin's browser after the reset; the expiry test reuses it (one sign-in less, see the note on rate limits). */
let adminContext: BrowserContext;
let adminPage: Page;

const school = { email: '', password: '', first: 'Selin', last: '', secret: '' };
let recoveryCodes: string[] = [];

test.beforeAll(async ({ browser }) => {
  platformContext = await browser.newContext();
  platformPage = await platformContext.newPage();
  school.last = `Güvenlik ${runId()}`;
  school.email = `mfa-admin-${runId()}@${emailDomain}`;
  school.password = newPassword();
});

test.afterAll(async () => {
  await adminContext?.close();
  await platformContext?.close();
});

async function fillCodeStep(page: Page, label: 'Doğrulama kodu' | 'Kurtarma kodu', code: string) {
  await page.getByLabel(label, { exact: true }).fill(code);
  await page.getByRole('button', { name: 'Doğrula', exact: true }).click();
}

async function signInWithPassword(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('E-posta', { exact: true }).fill(school.email);
  await page.getByLabel('Şifre', { exact: true }).fill(school.password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
}

async function readCodes(page: Page | Locator): Promise<string[]> {
  const items = page.getByRole('list', { name: 'Kurtarma kodları' }).getByRole('listitem');
  await expect(items).toHaveCount(10);
  return (await items.allTextContents()).map((text) => text.trim());
}

async function acknowledgeCodes(page: Page | Locator): Promise<void> {
  await page.getByLabel('Kurtarma kodlarını güvenli bir yere kaydettim.').check();
  await page.getByRole('button', { name: 'Devam et' }).click();
}

test('a platform admin signs in with a password and a code computed from the setup key', async () => {
  const testInfo = test.info();
  const email = process.env['E2E_PLATFORM_ADMIN_EMAIL']!;
  const password = process.env['E2E_PLATFORM_ADMIN_NEW_PASSWORD']!;
  const secret = process.env['E2E_PLATFORM_ADMIN_MFA_SECRET']!;
  const page = platformPage;
  await answerRefreshWithoutSession(platformContext);

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
  // The body is read on its way to the page (passed on unchanged): after the app navigates away the browser no longer
  // keeps it (as signIn in support/ui.ts does).
  let verifiedToken: string | undefined;
  await page.route('**/api/v1/auth/mfa/verify', async (route) => {
    const response = await route.fetch();
    if (response.ok()) verifiedToken = ((await response.json()) as { accessToken: string }).accessToken;
    await route.fulfill({ response });
  });
  await page.getByLabel('Doğrulama kodu').fill(totpCode(secret, 1));
  await page.getByRole('button', { name: 'Doğrula' }).click();
  await expect(page).toHaveURL(/\/platform\/schools$/);
  await page.unroute('**/api/v1/auth/mfa/verify');
  expect(verifiedToken).toBeTruthy();
  platformToken = verifiedToken!;

  // Two-step verification is mandatory for this role: the security page says so and offers no way to turn it off.
  await page.getByRole('link', { name: 'Güvenlik' }).click();
  await expect(page.getByRole('heading', { level: 2, name: 'İki adımlı doğrulama' })).toBeVisible();
  await expect(page.getByText('Açık', { exact: true })).toBeVisible();
  await expect(page.getByText('Platform yöneticisi hesapları için zorunlu')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Kapat' })).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'security-page');
});

test('a school admin turns it on, signs in with a code and with a recovery code, and replaces the recovery codes', async ({
  browser,
}) => {
  test.setTimeout(240_000);
  // Arranged through the API with the Platform Admin's session: a school of its own, so that the other specs'
  // School Admin never gets two-step verification.
  const platform = await ApiClient.withToken(platformToken);
  const created = await platform.post<{ temporaryPassword: string }>('/api/v1/platform/schools', {
    name: `E2E MFA Okulu ${runId()}`,
    slug: `e2e-mfa-${runId()}`,
    timeZone: schoolTimeZone,
    admin: { firstName: school.first, lastName: school.last, email: school.email },
  });
  await platform.dispose();
  const admin = await ApiClient.login(school.email, created.temporaryPassword);
  await admin.changePassword(created.temporaryPassword, school.password);
  await admin.dispose();

  const context = await browser.newContext();
  try {
    await answerRefreshWithoutSession(context);
    const page = await context.newPage();
    await page.goto('/login');
    await signIn(page, school.email, school.password);
    await expect(page).toHaveURL(/\/dashboard$/);

    // Turn it on from the security page: the QR code and the key, a code from the "app", the recovery codes once.
    await page.getByRole('link', { name: 'Güvenlik' }).click();
    await page.getByRole('button', { name: 'İki adımlı doğrulamayı aç' }).click();
    const key = page.getByLabel('Kurulum anahtarı');
    await expect(key).toBeVisible();
    school.secret = (await key.inputValue()).replace(/\s/g, '');
    const codes = new TotpSequence(school.secret);
    await page.locator('#mfa-setup-code').fill(await codes.next());
    await page.getByRole('button', { name: 'Doğrula ve aç' }).click();
    recoveryCodes = await readCodes(page);
    expect(new Set(recoveryCodes).size).toBe(10);
    await acknowledgeCodes(page);
    await expect(toast(page, 'İki adımlı doğrulama açıldı.')).toBeVisible();
    await expect(page.getByText('Kalan kurtarma kodu: 10.')).toBeVisible();

    // Turning it on ended every session. Sign out and in again: the password now leads to a code.
    await page.getByRole('button', { name: 'Çıkış yap' }).click();
    await signInWithPassword(page);
    await expect(
      page.getByRole('heading', { level: 1, name: 'İki adımlı doğrulama' }),
    ).toBeVisible();
    await fillCodeStep(page, 'Doğrulama kodu', await codes.next());
    await expect(page).toHaveURL(/\/dashboard$/);

    // A recovery code signs in too, once.
    await page.getByRole('button', { name: 'Çıkış yap' }).click();
    await signInWithPassword(page);
    await page.getByRole('button', { name: 'Kurtarma kodu kullan' }).click();
    await fillCodeStep(page, 'Kurtarma kodu', recoveryCodes[0]);
    await expect(page).toHaveURL(/\/dashboard$/);

    // Replace the recovery codes: a current code from the app (a recovery code is refused), then the new ones, once.
    await page.getByRole('link', { name: 'Güvenlik' }).click();
    await expect(page.getByText('Kalan kurtarma kodu: 9.')).toBeVisible();
    await page.getByRole('button', { name: 'Kurtarma kodlarını yenile' }).click();
    const regenerate = dialog(page);
    await regenerate.getByLabel('Doğrulama kodu', { exact: true }).fill(recoveryCodes[1]);
    await regenerate.getByRole('button', { name: 'Yeni kodlar oluştur' }).click();
    await expect(regenerate.getByRole('alert')).toContainText('Doğrulama kodu hatalı.');
    await regenerate.getByLabel('Doğrulama kodu', { exact: true }).fill(await codes.next());
    await regenerate.getByRole('button', { name: 'Yeni kodlar oluştur' }).click();
    const fresh = await readCodes(regenerate);
    expect(fresh.filter((code) => recoveryCodes.includes(code))).toEqual([]);
    await acknowledgeCodes(regenerate);
    await expect(toast(page, 'Kurtarma kodları yenilendi; eskileri artık geçersiz.')).toBeVisible();
    await expect(page.getByText('Kalan kurtarma kodu: 10.')).toBeVisible();
  } finally {
    await context.close();
  }
});

test('a platform admin resets a school admin’s two-step verification and the admin signs in with the password alone', async ({
  browser,
}) => {
  const testInfo = test.info();
  const page = platformPage;
  await page.getByRole('link', { name: 'Okullar' }).click();
  const reset = page.getByRole('button', {
    name: new RegExp(`İki adımlı doğrulamayı sıfırla: .*${school.last}`),
  });
  await expect(reset).toBeVisible();
  await reset.click();
  const confirm = dialog(page);
  await expect(
    confirm.getByRole('heading', { name: 'İki adımlı doğrulamayı sıfırla' }),
  ).toBeVisible();
  await confirm.getByLabel('Neden').selectOption({ label: 'Telefon kayboldu' });
  await confirm.getByRole('button', { name: 'Sıfırla' }).click();
  await expect(toast(page, 'İki adımlı doğrulama sıfırlandı.')).toBeVisible();
  await expect(reset).toHaveCount(0);

  // Their sessions ended and the code is gone: the password alone signs in, and the security page offers to turn it on.
  // A browser that never signed in first: the restore is refused with the same code as for an expired session, and the
  // page stays quiet (V1-03, DEC-690). The context stays open for the next test.
  adminContext = await browser.newContext();
  adminPage = await adminContext.newPage();
  await adminPage.goto('/login');
  await expect(adminPage.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
  await expect(adminPage.getByRole('alert')).toHaveCount(0);
  await signIn(adminPage, school.email, school.password);
  await expect(adminPage).toHaveURL(/\/dashboard$/);
  await adminPage.getByRole('link', { name: 'Güvenlik' }).click();
  await expect(adminPage.getByRole('button', { name: 'İki adımlı doğrulamayı aç' })).toBeVisible();

  // The Platform Admins page lists this account without a reset of its own (Mfa.CannotResetOwn).
  await page.getByRole('link', { name: 'Platform yöneticileri' }).click();
  const own = page.getByRole('row').filter({ hasText: process.env['E2E_PLATFORM_ADMIN_EMAIL']! });
  await expect(own).toBeVisible();
  await expect(own.getByText('Siz', { exact: true })).toBeVisible();
  await expect(own.getByRole('button')).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'platform-admins');
});

test('a reload after the session ran out says so, once', async () => {
  // The absolute limit passed: the browser dropped the refresh cookie. The reload lands on the sign-in page, with the notice.
  await adminContext.clearCookies();
  await adminPage.reload();
  await expect(adminPage).toHaveURL(/\/login/);
  await expect(adminPage.getByRole('alert')).toContainText(
    'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.',
  );

  // Shown once: the next reload of the sign-in page is quiet again.
  await adminPage.reload();
  await expect(adminPage.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
  await expect(adminPage.getByRole('alert')).toHaveCount(0);
});
