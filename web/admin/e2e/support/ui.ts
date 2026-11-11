import { BrowserContext, Locator, Page, expect } from '@playwright/test';

/**
 * Answers the app's start-up session restore (`POST /api/v1/auth/refresh`) in the browser with 401, as the API does
 * for a browser without a refresh cookie, so a context with no session yet does not spend the per-IP refresh budget
 * (10/min, NFR-SEC-14) that the session tests need. Returns a function that removes the route.
 */
export async function answerRefreshWithoutSession(
  context: BrowserContext,
): Promise<() => Promise<void>> {
  const pattern = '**/api/v1/auth/refresh';
  await context.route(pattern, (route) =>
    route.fulfill({
      status: 401,
      contentType: 'application/problem+json',
      body: JSON.stringify({ status: 401, title: 'Unauthorized' }),
    }),
  );
  return () => context.unroute(pattern);
}

/**
 * Signs in through the login form and returns the access token the API issued, so a test can check
 * where it did (not) end up. Leaves the page wherever the app navigates after login.
 */
export async function signIn(
  page: Page,
  email: string,
  password: string,
): Promise<{ accessToken: string; setCookie: string }> {
  // The response is read on its way to the page (passed on unchanged): after the app navigates away
  // the browser no longer keeps the body.
  let session: { accessToken: string; setCookie: string } | undefined;
  await page.route('**/api/v1/auth/login', async (route) => {
    const response = await route.fetch();
    if (response.ok()) {
      session = {
        accessToken: ((await response.json()) as { accessToken: string }).accessToken,
        setCookie: response
          .headersArray()
          .filter((h) => h.name.toLowerCase() === 'set-cookie')
          .map((h) => h.value)
          .join('\n'),
      };
    }
    await route.fulfill({ response });
  });
  await page.getByLabel('E-posta', { exact: true }).fill(email);
  await page.getByLabel('Şifre', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect.poll(() => session, { message: 'login answered 200 with a token' }).toBeDefined();
  await page.unroute('**/api/v1/auth/login');
  return session!;
}

/** The success toast, which the app shows after every save. */
export function toast(page: Page, text: string): Locator {
  return page.getByText(text, { exact: true });
}

/** The open CDK dialog (only one at a time in these flows). */
export function dialog(page: Page): Locator {
  return page.getByRole('dialog');
}

/** Values the page's scripts can read: Web Storage and non-HttpOnly cookies (NFR-SEC-06). */
export function scriptReadableState(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const values: string[] = [document.cookie];
    for (const storage of [window.localStorage, window.sessionStorage]) {
      for (let i = 0; i < storage.length; i++) {
        const key = storage.key(i)!;
        values.push(key, storage.getItem(key) ?? '');
      }
    }
    return values;
  });
}
