import { randomUUID } from 'node:crypto';

import { Browser, BrowserContext, Page, devices, expect, request, test } from '@playwright/test';

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
import { answerRefreshWithoutSession, signIn, toast } from '../support/ui';
import { apiUrl, appUrl } from '../support/urls';

/**
 * Phase 3 exit criteria (P3-R1, docs/roadmap.md), end to end with two people at once: the School Admin in the admin
 * panel (a desktop browser context) and the parent in the parent app (this project's phone-sized page).
 *
 * 1. The School Admin invites the guardian from the student page; the parent accepts the invitation in the parent app,
 *    signs in, sees the child's attendance and pays the due online (Fake sandbox provider, DEC-347); the payment then
 *    shows as paid in the parent app and in the admin panel.
 * 2. An announcement the School Admin publishes from the group page reaches the parent's open app live (SignalR, P3-08)
 *    without a reload.
 * 3. Both languages work (P3-10): the admin panel and the parent app switch tr ↔ en on their key screens, and API
 *    problem texts follow Accept-Language (DEC-430).
 * 4. axe finds no serious or critical WCAG 2.x A/AA issue on the screens visited, in both languages (NFR-USA-02).
 *
 * Arranged through the API with the admin session's own access token (no extra login; per-IP auth budget, NFR-SEC-14):
 * a group whose only weekly slot is today (00:00–23:59, school time zone) with a monthly fee from this month, a student
 * enrolled from today with one guardian (unique .test address, synthetic data) and today's attendance as present.
 */
const fee = '750.00';

interface Arranged {
  studentId: string;
  childName: string;
  guardianId: string;
  guardianEmail: string;
  groupId: string;
  groupName: string;
  enrollmentId: string;
}

test('phase 3 exit: invitation, attendance, online payment, live announcement, two languages, axe', async ({
  browser,
  page,
  context,
}, testInfo) => {
  test.setTimeout(240_000);
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 10 || toMidnight > 24 * 60 - 5,
    'Skipped near midnight in Europe/Istanbul: the day and the period of the due would change mid-test.',
  );

  // ---- The School Admin signs in to the admin panel (desktop) ----
  const adminContext = await desktopAdminContext(browser);
  let api: ApiClient | undefined;
  try {
    const admin = await adminContext.newPage();
    const allowAdminRefresh = await answerRefreshWithoutSession(adminContext);
    await admin.goto('/login');
    const credentials = schoolAdmin();
    const session = await signIn(admin, credentials.email, credentials.password);
    await allowAdminRefresh();
    await expect(admin).toHaveURL(/\/dashboard$/);
    await expect(admin.getByRole('heading', { level: 1, name: 'Panel' })).toBeVisible();
    await expectNoSeriousA11yViolations(admin, testInfo, 'admin-dashboard');

    api = await ApiClient.withToken(session.accessToken);
    const arranged = await arrange(api);

    // ---- Criterion 3 (API): problem texts follow Accept-Language; the code never changes ----
    await expectProblemLanguage(session.accessToken);

    // ---- Criterion 1: the School Admin invites the guardian from the student page ----
    await admin.goto(`/students/${arranged.studentId}`);
    await expect(admin.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();
    const badge = admin.getByTestId(`invitation-status-${arranged.guardianId}`);
    await expect(badge).toHaveText('Davet edilmedi');
    await admin.getByRole('button', { name: 'Veli uygulamasına davet et' }).click();
    await expect(
      toast(admin, `Davet ${arranged.guardianEmail} adresine gönderildi.`),
    ).toBeVisible();
    await expect(badge).toHaveText('Davet gönderildi');
    await expectNoSeriousA11yViolations(admin, testInfo, 'admin-student-invited');

    // ---- Criterion 3 (admin panel): the student page in English and back, without a reload ----
    await admin.getByRole('button', { name: 'English' }).click();
    await expect(admin.locator('html')).toHaveAttribute('lang', 'en');
    await expect(admin.getByRole('heading', { level: 2, name: 'Guardians' })).toBeVisible();
    await expect(badge).toHaveText('Invitation sent');
    await expect(admin.getByRole('link', { name: 'Students', exact: true })).toBeVisible();
    await expect(admin.getByRole('link', { name: 'Payments', exact: true })).toBeVisible();
    await expect(admin.getByRole('button', { name: 'Sign out' })).toBeVisible();
    await expectNoSeriousA11yViolations(admin, testInfo, 'admin-student-en');
    await admin.getByRole('button', { name: 'Türkçe' }).click();
    await expect(admin.locator('html')).toHaveAttribute('lang', 'tr');
    await expect(admin.getByRole('heading', { level: 2, name: 'Veliler' })).toBeVisible();
    await expect(badge).toHaveText('Davet gönderildi');

    // ---- Criterion 1: the parent accepts the invitation in the parent app and signs in ----
    const link = await waitForLink(
      arranged.guardianEmail,
      /https:\/\/localhost:\d+\/reset-password#token=[^\s"<>]+/,
    );
    const allowParentRefresh = await answerRefreshWithoutSession(context);
    await page.goto(link);
    await expect(page.getByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeVisible();
    const password = newPassword();
    await page.getByLabel('Yeni şifre', { exact: true }).fill(password);
    await page.getByLabel('Yeni şifre (tekrar)', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Şifreyi kaydet' }).click();
    await expect(page.getByText('Şifreniz kaydedildi.', { exact: false })).toBeVisible();
    await page.getByRole('link', { name: 'Giriş yap' }).click();
    await expect(page.getByRole('heading', { name: 'Veli girişi' })).toBeVisible();
    await allowParentRefresh();
    await page.getByLabel('E-posta', { exact: true }).fill(arranged.guardianEmail);
    await page.getByLabel('Şifre', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Giriş yap' }).click();

    // The child, today's attendance and the unpaid due.
    const card = page.getByRole('article', { name: arranged.childName });
    await expect(card.getByText('Bugün antrenmana katıldı')).toBeVisible();
    await expect(card.getByTestId('owed-total')).toHaveText('₺750,00');
    await card.getByRole('link', { name: `${arranged.childName}: ayrıntılar` }).click();
    await expect(page.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();
    await expect(
      page.getByRole('region', { name: 'Yoklama geçmişi' }).getByText('Katıldı', { exact: true }),
    ).toBeVisible();
    const dues = page.getByRole('region', { name: 'Aidatlar' });
    await expect(dues.getByTestId('due-row')).toHaveCount(1);

    // Pays online on the Fake provider's sandbox checkout.
    await dues.getByRole('button', { name: /aidatını online öde/ }).click();
    await expect(page.getByRole('heading', { name: 'Sandbox ödeme sayfası' })).toBeVisible();
    await page.getByRole('button', { name: 'Ödemeyi onayla' }).click();
    await expect(page).toHaveURL(/\/odeme-sonucu\?durum=Succeeded$/);
    await expect(page.getByTestId('result-message')).toHaveText(
      'Ödemeniz alındı. Teşekkür ederiz.',
    );

    // Paid in the parent app: nothing owed, the payment listed as Online.
    await page.getByRole('link', { name: 'Çocuklarıma dön' }).click();
    await expect(card.getByText('Ödenmemiş aidat yok')).toBeVisible();
    await card.getByRole('link', { name: `${arranged.childName}: ayrıntılar` }).click();
    await expect(dues.getByText('Ödenmemiş aidat yok.')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Ödemeler' }).getByText(/Online/)).toBeVisible();
    await expectNoSeriousA11yViolations(page, testInfo, 'parent-child-paid');

    // Paid in the admin panel: the guardian's account is active, the payment is listed once as Online, the due is gone.
    // Leave the student page first: re-entering it reloads the guardian's status.
    await nav(admin, 'Panel');
    await expect(admin.getByRole('heading', { level: 1, name: 'Panel' })).toBeVisible();
    await navigateInApp(admin, `/students/${arranged.studentId}`);
    await expect(badge).toHaveText('Aktif');
    await nav(admin, 'Ödemeler');
    await expect(admin.getByRole('heading', { level: 1, name: 'Ödemeler' })).toBeVisible();
    await admin.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
    const paymentRow = admin.getByRole('row', { name: new RegExp(arranged.childName) });
    await expect(paymentRow).toHaveCount(1);
    await expect(paymentRow.getByRole('cell', { name: 'Online', exact: true })).toBeVisible();
    await expectNoSeriousA11yViolations(admin, testInfo, 'admin-payments');
    await nav(admin, 'Ödenmemiş borçlar');
    // The payments page has a 'Grup' select too: wait for the dues page before using the label.
    await expect(admin.getByRole('heading', { level: 1, name: 'Ödenmemiş borçlar' })).toBeVisible();
    // Wait for the group-filtered response: until it lands the page still shows the unfiltered list.
    const filtered = admin.waitForResponse(
      (r) =>
        r.url().includes('/api/v1/dues/outstanding') &&
        r.url().includes(`groupId=${arranged.groupId}`),
      { timeout: 15_000 },
    );
    await admin.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
    await filtered;
    await expect(
      admin.getByText('Bu filtrelerde ödenmemiş borç yok.', { exact: true }),
    ).toBeVisible();
    const outstanding = await api.get<{ items: unknown[] }>(
      `/api/v1/dues/outstanding?groupId=${arranged.groupId}&pageSize=100`,
    );
    expect(outstanding.items).toEqual([]);
    const recorded = await api.get<{
      items: { enrollmentId: string; method: string; status: string; amount: { amount: string } }[];
    }>(`/api/v1/payments?groupId=${arranged.groupId}&pageSize=100`);
    expect(
      recorded.items.map((p) => [p.enrollmentId, p.method, p.status, p.amount.amount]),
    ).toEqual([[arranged.enrollmentId, 'Online', 'Recorded', fee]]);

    // ---- Criterion 2: an announcement published in the admin panel reaches the open parent app live ----
    // A marker on the parent's window: a reload or a full navigation would drop it.
    await page.evaluate(() => {
      (window as unknown as { __p3r1?: string }).__p3r1 = 'same document';
    });
    const parentUrlBefore = page.url();
    await navigateInApp(admin, `/groups/${arranged.groupId}`);
    await expect(admin.getByRole('heading', { level: 1, name: arranged.groupName })).toBeVisible();
    const live = unique('Canlı çıkış duyurusu', testInfo);
    const form = admin.getByRole('region', { name: 'Grup duyuruları' });
    await form.getByLabel('Başlık', { exact: true }).fill(live);
    await form.getByLabel('Duyuru metni', { exact: true }).fill('Cumartesi antrenmanı 10:00’da.');
    await form.getByRole('button', { name: 'Duyuru yayımla' }).click();
    await expect(toast(admin, 'Duyuru yayımlandı.')).toBeVisible();

    await expect(page.getByText('Yeni duyuru yayımlandı')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('region', { name: 'Duyurular' }).getByText(live)).toBeVisible();
    expect(page.url()).toBe(parentUrlBefore);
    expect(
      await page.evaluate(() => (window as unknown as { __p3r1?: string }).__p3r1),
      'the parent app was not reloaded',
    ).toBe('same document');

    // ---- Criterion 3 (parent app): the child's page in English and back, without a reload ----
    await page.getByRole('button', { name: 'English' }).click();
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
    await expect(page.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();
    await expect(
      page
        .getByRole('region', { name: 'Attendance history' })
        .getByText('Attended', { exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole('region', { name: 'Monthly fees' }).getByText('No unpaid monthly fees.'),
    ).toBeVisible();
    await expect(page.getByRole('region', { name: 'Payments' }).getByText(/Online/)).toBeVisible();
    await expect(page.getByRole('region', { name: 'Announcements' }).getByText(live)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
    await expectNoSeriousA11yViolations(page, testInfo, 'parent-child-en');
    await page.getByRole('button', { name: 'Türkçe' }).click();
    await expect(page.locator('html')).toHaveAttribute('lang', 'tr');
    await expect(page.getByRole('region', { name: 'Yoklama geçmişi' })).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { __p3r1?: string }).__p3r1)).toBe(
      'same document',
    );

    // Both sign out, so the sessions end with the test.
    await page.getByRole('button', { name: 'Çıkış yap' }).click();
    await expect(page.getByRole('heading', { name: 'Veli girişi' })).toBeVisible();
    await admin.getByRole('button', { name: 'Çıkış yap' }).click();
    await expect(admin).toHaveURL(/\/login$/);
  } finally {
    await api?.dispose();
    await adminContext.close();
  }
});

/**
 * A desktop context for the admin panel. Contexts made from the `browser` fixture take this project's phone settings,
 * so the device is overridden here (the admin panel's School Admin flows run on desktop browsers, NFR-CMP-01).
 */
async function desktopAdminContext(browser: Browser): Promise<BrowserContext> {
  const desktop = devices['Desktop Chrome'];
  return browser.newContext({
    userAgent: desktop.userAgent,
    viewport: desktop.viewport,
    deviceScaleFactor: desktop.deviceScaleFactor,
    isMobile: false,
    hasTouch: false,
    baseURL: appUrl,
    ignoreHTTPSErrors: true,
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
  });
}

/**
 * Moves within the admin SPA without a document load. A reload restores the session through the refresh cookie; doing
 * that repeatedly in one long test races the app's own token refresh, which is not what this test is about.
 */
async function navigateInApp(page: Page, path: string): Promise<void> {
  await page.evaluate((target) => {
    history.pushState(null, '', target);
    window.dispatchEvent(new PopStateEvent('popstate', { state: null }));
  }, path);
  await expect(page).toHaveURL((url) => url.pathname === path);
}

async function nav(page: Page, label: string): Promise<void> {
  await page.getByRole('link', { name: label, exact: true }).click();
}

async function arrange(api: ApiClient): Promise<Arranged> {
  const testInfo = test.info();
  const today = todayInSchool();
  const period = today.date.slice(0, 7);

  const sport = await api.post<{ id: string }>('/api/v1/sports', {
    name: unique('Basketbol', testInfo),
  });
  const facility = await api.post<{ id: string }>('/api/v1/facilities', {
    name: unique('Spor Salonu', testInfo),
    type: 'Court',
    address: { line: 'Deneme Cad. No:5', district: 'Merkez', city: 'Eskişehir' },
    capacity: null,
  });
  const groupName = unique('U11 Basketbol', testInfo);
  const group = await api.post<{ id: string; version: number }>('/api/v1/groups', {
    sportId: sport.id,
    name: groupName,
    birthYearRange: { from: 2014, to: 2017 },
    capacity: 20,
    facilityId: facility.id,
    coachId: null,
  });
  await api.put(`/api/v1/groups/${group.id}/schedule`, {
    slots: [{ day: today.day, start: '00:00', end: '23:59' }],
    version: group.version,
  });
  await api.post(`/api/v1/groups/${group.id}/fees`, {
    effectiveFrom: period,
    amount: { amount: fee, currency: 'TRY' },
  });

  const lastName = unique('Çıkış', testInfo);
  const guardianEmail = uniqueEmail('cikis-veli', testInfo);
  const guardianPhone = phone(testInfo, 310);
  const student = await api.post<{ id: string; guardians: { guardianId: string }[] }>(
    '/api/v1/students',
    {
      firstName: 'Arda',
      lastName,
      birthDate: '2015-09-01',
      gender: 'Male',
      phone: null,
      email: null,
      emergencyContact: { name: `Veli ${lastName}`, phone: guardianPhone, relationship: 'Baba' },
      guardians: [
        {
          guardianId: null,
          newGuardian: {
            firstName: 'Kerem',
            lastName,
            phone: guardianPhone,
            email: guardianEmail,
            address: null,
          },
          relationship: 'Father',
        },
      ],
    },
  );
  const enrollment = await api.post<{ id: string }>('/api/v1/enrollments', {
    studentId: student.id,
    groupId: group.id,
    startDate: today.date,
  });
  await api.put(`/api/v1/groups/${group.id}/sessions/${today.date}T00:00/attendance`, {
    records: [{ studentId: student.id, status: 'Present' }],
  });

  return {
    studentId: student.id,
    childName: `Arda ${lastName}`,
    guardianId: student.guardians[0].guardianId,
    guardianEmail,
    groupId: group.id,
    groupName,
    enrollmentId: enrollment.id,
  };
}

/** DEC-430: `title` and `detail` follow Accept-Language (Turkish by default); `code` stays the contract. */
async function expectProblemLanguage(accessToken: string): Promise<void> {
  const client = await request.newContext({ baseURL: apiUrl });
  try {
    const missing = `/api/v1/students/${randomUUID()}`;
    const read = async (language?: string) => {
      const response = await client.get(missing, {
        headers: {
          Authorization: `Bearer ${accessToken}`,
          ...(language ? { 'Accept-Language': language } : {}),
        },
      });
      expect(response.status()).toBe(404);
      const body = (await response.json()) as { code: string; title: string; detail: string };
      return [body.code, body.title, body.detail];
    };
    const turkish = ['Student.NotFound', 'Bulunamadı', 'Öğrenci bulunamadı.'];
    const english = ['Student.NotFound', 'Not found', 'Student not found.'];
    expect(await read('en')).toEqual(english);
    expect(await read('en-GB,en;q=0.9,tr;q=0.5')).toEqual(english);
    expect(await read('tr-TR')).toEqual(turkish);
    expect(await read('de-DE')).toEqual(turkish);
    expect(await read()).toEqual(turkish);
  } finally {
    await client.dispose();
  }
}
