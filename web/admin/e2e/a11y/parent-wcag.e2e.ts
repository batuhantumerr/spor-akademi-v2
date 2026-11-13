import { expect, test } from '../support/test';

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
import { answerRefreshWithoutSession } from '../support/ui';
import { A11yAudit } from '../support/wcag';

/**
 * P3-11 (DEC-436..DEC-445): WCAG 2.2 AA audit of the parent app's screens in Turkish and English, light and dark,
 * desktop and 360 px (docs/erisilebilirlik-denetimi.md). One parent session; the School Admin arranges a child with
 * today's attendance, an unpaid due and an announcement through the API, and invites the guardian.
 */
test('parent app screens meet WCAG 2.2 AA', async ({ page, context }, testInfo) => {
  test.setTimeout(10 * 60_000);
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 10 || toMidnight > 24 * 60 - 5,
    'Skipped near midnight in Europe/Istanbul: the arranged day and due would change.',
  );
  const audit = new A11yAudit('parent');
  try {
    const arranged = await arrange();
    const link = await waitForLink(
      arranged.guardianEmail,
      /https:\/\/localhost:\d+\/reset-password#token=[^\s"<>]+/,
    );
    const allowRefresh = await answerRefreshWithoutSession(context);

    await test.step('forgot password (signed out)', async () => {
      await page.goto('/forgot-password');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await audit.screen(page, 'forgot-password');
    });

    const password = newPassword();
    await test.step('set password from the invitation link', async () => {
      await page.goto(link);
      await expect(page.getByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeVisible();
      await audit.screen(page, 'reset-password');
      await page.getByLabel('Yeni şifre', { exact: true }).fill(password);
      await page.getByLabel('Yeni şifre (tekrar)', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Şifreyi kaydet' }).click();
      await expect(page.getByText('Şifreniz kaydedildi.', { exact: false })).toBeVisible();
      await page.getByRole('link', { name: 'Giriş yap' }).click();
    });

    await test.step('login', async () => {
      await expect(page.getByRole('heading', { name: 'Veli girişi' })).toBeVisible();
      await audit.screen(page, 'login');
      await allowRefresh();
      await page.getByLabel('E-posta', { exact: true }).fill(arranged.guardianEmail);
      await page.getByLabel('Şifre', { exact: true }).fill(password);
      await page.getByRole('button', { name: 'Giriş yap' }).click();
    });

    const card = page.getByRole('article', { name: arranged.childName });
    await test.step('children', async () => {
      await expect(card).toBeVisible();
      await expect(card.getByTestId('owed-total')).toBeVisible();
      await audit.screen(page, 'children');
    });

    await test.step('child detail; route change moves focus to the page', async () => {
      await card.getByRole('link', { name: `${arranged.childName}: ayrıntılar` }).click();
      await expect(page.getByRole('heading', { level: 1, name: arranged.childName })).toBeVisible();
      await expect(page.locator('main')).toBeFocused();
      await expect(
        page.getByRole('region', { name: 'Aidatlar' }).getByTestId('due-row'),
      ).toHaveCount(1);
      await audit.screen(page, 'child-detail');
    });

    await test.step('pay online: sandbox checkout', async () => {
      await page
        .getByRole('region', { name: 'Aidatlar' })
        .getByRole('button', { name: /aidatını online öde/ })
        .click();
      await expect(page.getByRole('heading', { name: 'Sandbox ödeme sayfası' })).toBeVisible();
      await audit.screen(page, 'sandbox-checkout');
      await page.getByRole('button', { name: 'Ödemeyi onayla' }).click();
      await expect(page).toHaveURL(/\/odeme-sonucu\?durum=Succeeded$/);
      await audit.screen(page, 'payment-result');
      await page.getByRole('link', { name: 'Çocuklarıma dön' }).click();
      await expect(card.getByText('Ödenmemiş aidat yok')).toBeVisible();
    });

    await test.step('offline state', async () => {
      // The service worker serves the app shell; no API answer is cached (P3-07).
      await page.evaluate(async () => navigator.serviceWorker.ready);
      await context.setOffline(true);
      try {
        await page.reload();
        await expect(page.getByRole('heading', { level: 1, name: 'Bağlantı yok' })).toBeVisible();
        await audit.screen(page, 'offline');
      } finally {
        await context.setOffline(false);
      }
    });
  } finally {
    await audit.writeReport(testInfo);
  }
});

interface Arranged {
  childName: string;
  guardianEmail: string;
}

async function arrange(): Promise<Arranged> {
  const testInfo = test.info();
  const credentials = schoolAdmin();
  const admin = await ApiClient.login(credentials.email, credentials.password);
  try {
    const today = todayInSchool();
    const sport = await admin.post<{ id: string }>('/api/v1/sports', {
      name: unique('Jimnastik', testInfo),
    });
    const facility = await admin.post<{ id: string }>('/api/v1/facilities', {
      name: unique('Jimnastik Salonu', testInfo),
      type: 'Court',
      address: { line: 'Deneme Cad. No:6', district: 'Atakum', city: 'Samsun' },
      capacity: null,
    });
    const group = await admin.post<{ id: string; version: number }>('/api/v1/groups', {
      sportId: sport.id,
      name: unique('U9 Jimnastik', testInfo),
      birthYearRange: { from: 2015, to: 2018 },
      capacity: 15,
      facilityId: facility.id,
      coachId: null,
    });
    await admin.put(`/api/v1/groups/${group.id}/schedule`, {
      slots: [{ day: today.day, start: '00:00', end: '23:59' }],
      version: group.version,
    });
    await admin.post(`/api/v1/groups/${group.id}/fees`, {
      effectiveFrom: today.date.slice(0, 7),
      amount: { amount: '650.00', currency: 'TRY' },
    });
    const lastName = unique('Erişim', testInfo);
    const guardianEmail = uniqueEmail('erisim-veli', testInfo);
    const guardianPhone = phone(testInfo, 500);
    const student = await admin.post<{ id: string; guardians: { guardianId: string }[] }>(
      '/api/v1/students',
      {
        firstName: 'Mert',
        lastName,
        birthDate: '2016-09-01',
        gender: 'Male',
        phone: null,
        email: null,
        emergencyContact: { name: `Veli ${lastName}`, phone: guardianPhone, relationship: 'Baba' },
        guardians: [
          {
            guardianId: null,
            newGuardian: {
              firstName: 'Kaan',
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
    await admin.post('/api/v1/enrollments', {
      studentId: student.id,
      groupId: group.id,
      startDate: today.date,
    });
    await admin.put(`/api/v1/groups/${group.id}/sessions/${today.date}T00:00/attendance`, {
      records: [{ studentId: student.id, status: 'Present' }],
    });
    await admin.post(`/api/v1/groups/${group.id}/announcements`, {
      title: unique('Salon değişikliği', testInfo),
      body: 'Bu hafta antrenmanlar B salonunda.',
    });
    await admin.post(
      `/api/v1/guardians/${student.guardians[0].guardianId}/invitation`,
      undefined,
      200,
    );
    return { childName: `Mert ${lastName}`, guardianEmail };
  } finally {
    await admin.dispose();
  }
}
