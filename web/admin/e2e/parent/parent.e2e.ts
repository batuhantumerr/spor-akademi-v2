import { expect, test } from '@playwright/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { ApiClient } from '../support/api';
import {
  minutesToSchoolMidnight,
  newPassword,
  phone,
  schoolAdmin,
  todayInSchool,
  unique,
  uniqueEmail,
} from '../support/data';
import { waitForLink } from '../support/mail';
import { answerRefreshWithoutSession, scriptReadableState } from '../support/ui';

/**
 * The parent's first visit, end to end (P3-05, P3-06; phase 3 exit criterion, P3-R1): the School Admin invites a
 * guardian; the invitation e-mail arrives; the parent sets a password from its link, signs in on a phone, sees the
 * child with today's attendance and the unpaid due, and pays it online through the Fake sandbox provider.
 *
 * Arranged through the API as the School Admin: a group whose only weekly slot is today (00:00–23:59, school time
 * zone) with a monthly fee from this month, one student enrolled from today whose guardian has a unique .test
 * address, today's attendance recorded as present, and an announcement.
 */
const fee = '750.00';

interface Arranged {
  childName: string;
  groupName: string;
  guardianEmail: string;
  announcement: string;
  enrollmentId: string;
}

let arranged: Arranged;
let admin: ApiClient | undefined;

test.beforeAll(async () => {
  const testInfo = test.info();
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 5 || toMidnight > 24 * 60 - 5,
    'Skipped within 5 minutes of midnight in Europe/Istanbul: the day and the period of the due would change.',
  );
  const credentials = schoolAdmin();
  admin = await ApiClient.login(credentials.email, credentials.password);
  const today = todayInSchool();
  const period = today.date.slice(0, 7);

  const sport = await admin.post<{ id: string }>('/api/v1/sports', {
    name: unique('Yüzme', testInfo),
  });
  const facility = await admin.post<{ id: string }>('/api/v1/facilities', {
    name: unique('Kapalı Havuz', testInfo),
    type: 'Pool',
    address: { line: 'Deneme Cad. No:4', district: 'Nilüfer', city: 'Bursa' },
    capacity: null,
  });
  const groupName = unique('U10 Yüzme', testInfo);
  const group = await admin.post<{ id: string; version: number }>('/api/v1/groups', {
    sportId: sport.id,
    name: groupName,
    birthYearRange: { from: 2014, to: 2017 },
    capacity: 20,
    facilityId: facility.id,
    coachId: null,
  });
  await admin.put(`/api/v1/groups/${group.id}/schedule`, {
    slots: [{ day: today.day, start: '00:00', end: '23:59' }],
    version: group.version,
  });
  await admin.post(`/api/v1/groups/${group.id}/fees`, {
    effectiveFrom: period,
    amount: { amount: fee, currency: 'TRY' },
  });

  const lastName = unique('Veli', testInfo);
  const guardianEmail = uniqueEmail('veli', testInfo);
  const guardianPhone = phone(testInfo, 300);
  const student = await admin.post<{ id: string; guardians: { guardianId: string }[] }>(
    '/api/v1/students',
    {
      firstName: 'Ece',
      lastName,
      birthDate: '2016-06-01',
      gender: 'Female',
      phone: null,
      email: null,
      emergencyContact: { name: `Veli ${lastName}`, phone: guardianPhone, relationship: 'Anne' },
      guardians: [
        {
          guardianId: null,
          newGuardian: {
            firstName: 'Deniz',
            lastName,
            phone: guardianPhone,
            email: guardianEmail,
            address: null,
          },
          relationship: 'Mother',
        },
      ],
    },
  );
  const enrollment = await admin.post<{ id: string }>('/api/v1/enrollments', {
    studentId: student.id,
    groupId: group.id,
    startDate: today.date,
  });
  await admin.put(`/api/v1/groups/${group.id}/sessions/${today.date}T00:00/attendance`, {
    records: [{ studentId: student.id, status: 'Present' }],
  });
  const announcement = unique('Havuz bakımı', testInfo);
  await admin.post(`/api/v1/groups/${group.id}/announcements`, {
    title: announcement,
    body: 'Cuma günü antrenman yapılmayacak.',
  });
  // DEC-383: the invitation e-mail goes out from the outbox after the commit.
  await admin.post(
    `/api/v1/guardians/${student.guardians[0].guardianId}/invitation`,
    undefined,
    200,
  );

  arranged = {
    childName: `Ece ${lastName}`,
    groupName,
    guardianEmail,
    announcement,
    enrollmentId: enrollment.id,
  };
});

test.afterAll(async () => {
  await admin?.dispose();
});

test('an invited parent sets a password, signs in, sees the child and pays the due online', async ({
  page,
  context,
}, testInfo) => {
  // The invitation link: the token is in the fragment, the page removes it at once (DEC-351).
  const link = await waitForLink(
    arranged.guardianEmail,
    /https:\/\/localhost:\d+\/reset-password#token=[^\s"<>]+/,
  );
  // A browser without a session yet: answer the start-up refresh locally (rate limit budget, NFR-SEC-14).
  const allowRefresh = await answerRefreshWithoutSession(context);
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeVisible();
  await expect(page).toHaveURL(/\/reset-password$/);
  await expectNoSeriousA11yViolations(page, testInfo, 'parent-set-password');

  const password = newPassword();
  await page.getByLabel('Yeni şifre', { exact: true }).fill(password);
  await page.getByLabel('Yeni şifre (tekrar)', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Şifreyi kaydet' }).click();
  await expect(page.getByText('Şifreniz kaydedildi.', { exact: false })).toBeVisible();
  await page.getByRole('link', { name: 'Giriş yap' }).click();

  await expect(page.getByRole('heading', { name: 'Veli girişi' })).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'parent-login');
  await allowRefresh();
  await page.getByLabel('E-posta', { exact: true }).fill(arranged.guardianEmail);
  await page.getByLabel('Şifre', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();

  // Çocuklarım: the ten-second view (next training, today's attendance, what is owed).
  const card = page.getByRole('article', { name: arranged.childName });
  await expect(card).toBeVisible();
  await expect(card.getByText('Şu anda: 00:00–23:59')).toBeVisible();
  await expect(card.getByText('Bugün antrenmana katıldı')).toBeVisible();
  await expect(card.getByTestId('owed-total')).toHaveText('₺750,00');
  await expectNoSeriousA11yViolations(page, testInfo, 'parent-children');

  // NFR-SEC-06: the access token is in memory only; the refresh cookie is HttpOnly.
  const readable = (await scriptReadableState(page)).join('\n');
  expect(readable, 'no JWT in Web Storage or document.cookie').not.toMatch(/eyJ[\w-]+\.[\w-]+\./);
  const refresh = (await context.cookies()).find((c) => c.name === 'sa_refresh');
  expect(refresh?.httpOnly).toBe(true);

  await card.getByRole('link', { name: `${arranged.childName}: ayrıntılar` }).click();
  await expect(page.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();
  const attendance = page.getByRole('region', { name: 'Yoklama geçmişi' });
  await expect(attendance.getByText('Katıldı', { exact: true })).toBeVisible();
  await expect(
    page.getByRole('region', { name: 'Duyurular' }).getByText(arranged.announcement),
  ).toBeVisible();
  const dues = page.getByRole('region', { name: 'Aidatlar' });
  await expect(dues.getByTestId('due-row')).toHaveCount(1);
  await expectNoSeriousA11yViolations(page, testInfo, 'parent-child');

  // Pay online: the Fake provider's checkout is this app's /sandbox-odeme (DEC-347).
  await dues.getByRole('button', { name: /aidatını online öde/ }).click();
  await expect(page.getByRole('heading', { name: 'Sandbox ödeme sayfası' })).toBeVisible();
  await page.getByRole('button', { name: 'Ödemeyi onayla' }).click();
  await expect(page).toHaveURL(/\/odeme-sonucu\?durum=Succeeded$/);
  await expect(page.getByTestId('result-message')).toHaveText('Ödemeniz alındı. Teşekkür ederiz.');

  // Back in the app (the session is restored from the refresh cookie): nothing owed, the payment is listed.
  await page.getByRole('link', { name: 'Çocuklarıma dön' }).click();
  await expect(card.getByText('Ödenmemiş aidat yok')).toBeVisible();
  await card.getByRole('link', { name: `${arranged.childName}: ayrıntılar` }).click();
  await expect(
    page.getByRole('region', { name: 'Aidatlar' }).getByText('Ödenmemiş aidat yok.'),
  ).toBeVisible();
  const payments = page.getByRole('region', { name: 'Ödemeler' });
  await expect(payments.getByText(/Online/)).toBeVisible();

  // What the server recorded, read back as the School Admin: one online payment for the due.
  const recorded = await admin!.get<{
    items: { enrollmentId: string; method: string; amount: { amount: string } }[];
  }>(`/api/v1/payments?pageSize=100`);
  expect(
    recorded.items
      .filter((p) => p.enrollmentId === arranged.enrollmentId)
      .map((p) => [p.method, p.amount.amount]),
  ).toEqual([['Online', fee]]);

  // P3-07 (DEC-394): installable, and the service worker caches the app shell only. Offline, a reload still opens
  // the app (from the cache), which says "Bağlantı yok" and shows none of the family's data: no API answer was
  // ever cached.
  const manifestHref = await page.locator('link[rel="manifest"]').getAttribute('href');
  expect(manifestHref).toBe('/manifest.webmanifest');
  const manifestResponse = await page.request.get(manifestHref!);
  expect(manifestResponse.headers()['content-type']).toContain('application/manifest+json');
  const manifest = (await manifestResponse.json()) as {
    name: string;
    start_url: string;
    scope: string;
    display: string;
    icons: { sizes: string; purpose: string }[];
  };
  expect(manifest).toMatchObject({
    name: 'Spor Akademi Veli',
    start_url: '/',
    scope: '/',
    display: 'standalone',
  });
  expect(manifest.icons.map((icon) => `${icon.sizes} ${icon.purpose}`)).toEqual(
    expect.arrayContaining(['192x192 any', '512x512 any', '192x192 maskable', '512x512 maskable']),
  );
  const workerUrl = await page.evaluate(
    async () => (await navigator.serviceWorker.ready).active?.scriptURL,
  );
  expect(workerUrl).toBe(`${new URL(page.url()).origin}/sw.js`);
  const cachedPaths = await page.evaluate(async () => {
    const paths: string[] = [];
    for (const name of await caches.keys()) {
      const requests = await (await caches.open(name)).keys();
      paths.push(...requests.map((request) => new URL(request.url).pathname));
    }
    return paths;
  });
  expect(cachedPaths).toContain('/index.html');
  expect(cachedPaths.filter((path) => /^\/(api|health)(\/|$)/i.test(path))).toEqual([]);

  await context.setOffline(true);
  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: 'Bağlantı yok' })).toBeVisible();
  await expect(page.getByText(arranged.childName)).toHaveCount(0);
  await expect(page.getByText(arranged.announcement)).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'parent-offline');
  // Back online, the app restores the session by itself (one refresh call) and loads the data again.
  await context.setOffline(false);
  await expect(page.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();

  // Sign out: the cookie is gone.
  // The app ends the session locally first, then tells the server, which deletes the cookie.
  const loggedOut = page.waitForResponse('**/api/v1/auth/logout');
  await page.getByRole('button', { name: 'Çıkış yap' }).click();
  await expect(page.getByRole('heading', { name: 'Veli girişi' })).toBeVisible();
  expect((await loggedOut).status()).toBe(204);
  expect((await context.cookies()).find((c) => c.name === 'sa_refresh')).toBeUndefined();
});
