import { expect, test } from '../support/test';

import { answerRefreshWithoutSession } from '../support/ui';

/**
 * P3-10: switching to English on the login page changes the page without a reload and is remembered by the browser.
 * No sign-in, and the start-up refresh is answered in the browser, so it spends none of the per-IP auth budget.
 * One desktop browser is enough; the unit tests cover the rest.
 */
test('switching to English changes the login page and is remembered', async ({
  page,
  context,
  browserName,
}) => {
  test.skip(browserName !== 'chromium', 'The language switch is checked in one browser only.');
  await answerRefreshWithoutSession(context);

  await page.goto('/login');
  await expect(page.getByRole('heading', { level: 1, name: 'Giriş yap' })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('lang', 'tr');

  await page.getByRole('button', { name: 'English' }).click();

  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expect(page.getByLabel('Password', { exact: true })).toBeVisible();
  await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  await expect(page).toHaveTitle('Sign in · Spor Akademi');

  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Türkçe' })).toBeVisible();
});
