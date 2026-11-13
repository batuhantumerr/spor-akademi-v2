import { randomUUID } from 'node:crypto';

import { Page, expect, test } from '../support/test';

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
import { answerRefreshWithoutSession, dialog, signIn } from '../support/ui';
import { appUrl } from '../support/urls';
import { A11yAudit, Variant, applyVariant } from '../support/wcag';

/**
 * P3-11 (DEC-436..DEC-445): WCAG 2.2 AA audit of the admin panel's important screens, for the School Admin and the
 * coach, in Turkish and English, light and dark, desktop and 360 px (docs/erisilebilirlik-denetimi.md).
 *
 * One test with a step per screen: a single School Admin session (one login) and a single coach session cover every
 * screen, and a finding on one screen does not hide the others (soft assertions). The data behind the screens is
 * arranged through the API with the session's own access token.
 */
const fee = '750.00';

interface Arranged {
  groupName: string;
  coachEmail: string;
  coachPassword: string;
  students: { id: string; name: string }[];
}

test('admin panel screens meet WCAG 2.2 AA', async ({ page, context, browser }, testInfo) => {
  test.setTimeout(15 * 60_000);
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 10 || toMidnight > 24 * 60 - 5,
    'Skipped near midnight in Europe/Istanbul: the arranged session and due would change day or period.',
  );
  const audit = new A11yAudit('admin');
  try {
    const allowRefresh = await answerRefreshWithoutSession(context);
    await test.step('login and forgot password (signed out)', async () => {
      await page.goto('/forgot-password');
      await expect(page.getByRole('heading', { level: 1, name: 'Şifremi unuttum' })).toBeVisible();
      await audit.screen(page, 'forgot-password');
      await page.goto('/login');
      await expect(page.getByRole('heading', { level: 1, name: 'Giriş yap' })).toBeVisible();
      await audit.screen(page, 'login');
    });
    await allowRefresh();

    const admin = schoolAdmin();
    const session = await signIn(page, admin.email, admin.password);
    await expect(page).toHaveURL(/\/dashboard$/);
    const api = await ApiClient.withToken(session.accessToken);
    const arranged = await arrange(api);
    await api.dispose();

    await test.step('route changes are announced and move focus to the page', async () => {
      await nav(page, 'Gruplar');
      await expect(page.getByRole('heading', { level: 1, name: 'Gruplar' })).toBeVisible();
      await expect(page.getByTestId('route-announcer')).toHaveText('Gruplar · Spor Akademi');
      await expect(page.locator('main')).toBeFocused();
    });

    await test.step('dashboard', async () => {
      await nav(page, 'Panel');
      await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
      await expect(page.locator('app-dashboard-chart, table').first()).toBeVisible();
      await audit.screen(page, 'dashboard');
    });

    await test.step('mobile menu (drawer)', async () => {
      await applyVariant(page, { language: 'tr', theme: 'light', width: 'mobile' });
      const open = page.getByRole('button', { name: 'Menüyü aç' });
      await open.click();
      await expect(page.getByRole('button', { name: 'Menüyü kapat' })).toBeFocused();
      const mobileOnly = (['tr', 'en'] as const).flatMap((language) =>
        (['light', 'dark'] as const).map((theme): Variant => ({
          language,
          theme,
          width: 'mobile',
        })),
      );
      await audit.screen(page, 'mobile-menu', {
        variants: mobileOnly,
        keyboard: false,
        reflow: false,
      });
      // The audit ends on the desktop width, where the drawer closes by itself; open it again to close it with Escape.
      await applyVariant(page, { language: 'tr', theme: 'light', width: 'mobile' });
      await open.click();
      await expect(page.getByRole('button', { name: 'Menüyü kapat' })).toBeFocused();
      await page.keyboard.press('Escape');
      await expect(open).toBeFocused();
      await applyVariant(page, { language: 'tr', theme: 'light', width: 'desktop' });
    });

    for (const [label, name] of [
      ['Branşlar', 'sports'],
      ['Tesisler', 'facilities'],
      ['Antrenörler', 'coaches'],
    ] as const) {
      await test.step(name, async () => {
        await nav(page, label);
        await expect(page.getByRole('heading', { level: 1, name: label })).toBeVisible();
        await audit.screen(page, name);
      });
    }

    await test.step('groups and the group form dialog', async () => {
      await nav(page, 'Gruplar');
      await expect(page.getByRole('link', { name: arranged.groupName })).toBeVisible();
      await audit.screen(page, 'groups');
      const add = page.getByRole('button', { name: 'Grup ekle' });
      await add.click();
      await expect(dialog(page)).toBeVisible();
      await expectFocusInside(page, '[role="dialog"]');
      await audit.screen(page, 'group-form-dialog');
      await page.keyboard.press('Escape');
      await expect(dialog(page)).toBeHidden();
      await expect(add).toBeFocused();
    });

    await test.step('group detail with schedule and roster', async () => {
      await page.getByRole('link', { name: arranged.groupName }).click();
      await expect(page.getByRole('heading', { level: 1, name: arranged.groupName })).toBeVisible();
      await audit.screen(page, 'group-detail');
    });

    await test.step('students list and student detail', async () => {
      await nav(page, 'Öğrenciler');
      const student = arranged.students[0];
      await expect(page.getByRole('link', { name: student.name })).toBeVisible();
      await audit.screen(page, 'students');
      await page.getByRole('link', { name: student.name }).click();
      await expect(page.getByRole('heading', { level: 1, name: student.name })).toBeVisible();
      await audit.screen(page, 'student-detail');
    });

    await test.step('register student, with and without errors', async () => {
      await nav(page, 'Öğrenciler');
      await page.getByRole('link', { name: 'Öğrenci kaydet' }).click();
      await expect(page.getByRole('heading', { level: 1, name: 'Öğrenci kaydı' })).toBeVisible();
      await audit.screen(page, 'register-student');
      await page.getByRole('button', { name: 'Öğrenciyi kaydet' }).click();
      // 3.3.1 / 1.3.1: focus moves to the error summary, whose links lead to the fields; every invalid field names
      // its error.
      await expect(page.locator('#error-summary')).toBeFocused();
      await expect(page.locator('#error-summary a').first()).toBeVisible();
      await expectErrorsLinked(page);
      await audit.screen(page, 'register-student-errors', { keyboard: false });
    });

    await test.step('attendance month table', async () => {
      await nav(page, 'Yoklama');
      await expect(page.getByRole('heading', { level: 1, name: 'Yoklama' })).toBeVisible();
      await audit.screen(page, 'attendance');
    });

    await test.step('payments and the refund dialog', async () => {
      await nav(page, 'Ödemeler');
      const refund = page.getByRole('button', {
        name: new RegExp(`^İade et: ${arranged.students[0].name}`),
      });
      await expect(refund).toBeVisible();
      await audit.screen(page, 'payments');
      await refund.click();
      const confirm = page.getByRole('alertdialog').or(dialog(page));
      await expect(confirm).toBeVisible();
      await expectFocusInside(page, '[role="dialog"], [role="alertdialog"]');
      await audit.screen(page, 'refund-dialog');
      await page.keyboard.press('Escape');
      await expect(confirm).toBeHidden();
      await expect(refund).toBeFocused();
    });

    await test.step('dues and online payments', async () => {
      await nav(page, 'Ödenmemiş borçlar');
      await expect(
        page.getByRole('row', { name: new RegExp(arranged.students[1].name) }),
      ).toBeVisible();
      await audit.screen(page, 'dues');
      await nav(page, 'Online ödemeler');
      await expect(
        page.getByRole('row', { name: new RegExp(arranged.students[0].name) }),
      ).toBeVisible();
      await audit.screen(page, 'online-payments');
    });

    await test.step('reports: attendance, occupancy and revenue (P3-09)', async () => {
      await nav(page, 'Raporlar');
      await expect(page.getByRole('heading', { level: 1, name: 'Raporlar' })).toBeVisible();
      // The tab stays selected across the language, theme and width switches (page state, not the URL).
      for (const [tab, name] of [
        ['Devam', 'reports-attendance'],
        ['Doluluk', 'reports-occupancy'],
        ['Gelir', 'reports-revenue'],
      ] as const) {
        await page.getByRole('tab', { name: tab }).click();
        await expect(page.getByRole('tab', { name: tab })).toHaveAttribute('aria-selected', 'true');
        await expect(page.getByRole('tabpanel')).toBeVisible();
        await audit.screen(page, name);
      }
    });

    for (const [label, name] of [
      ['Veri saklama', 'settings-data-retention'],
      ['Aidat kuralları', 'settings-billing-rules'],
    ] as const) {
      await test.step(name, async () => {
        await nav(page, label);
        await expect(page.getByRole('heading', { level: 1, name: label })).toBeVisible();
        await audit.screen(page, name);
      });
    }

    await test.step('coach screens', async () => {
      const coachContext = await browser.newContext();
      try {
        const coach = await coachContext.newPage();
        await coach.setViewportSize({ width: 360, height: 780 });
        await coach.goto('/login');
        await signIn(coach, arranged.coachEmail, arranged.coachPassword);
        await expect(coach).toHaveURL(/\/change-password$/);
        await audit.screen(coach, 'change-password');
        const password = newPassword();
        await coach.getByLabel('Mevcut şifre', { exact: true }).fill(arranged.coachPassword);
        await coach.getByLabel('Yeni şifre', { exact: true }).fill(password);
        await coach.getByLabel('Yeni şifre (tekrar)', { exact: true }).fill(password);
        await coach.getByRole('button', { name: 'Şifreyi değiştir' }).click();
        await expect(coach).toHaveURL(/\/coach\/today$/);

        const open = coach.getByRole('link', { name: `Yoklama al: ${arranged.groupName} 00:00` });
        await expect(open).toBeVisible();
        await audit.screen(coach, 'coach-today');
        await open.click();
        await expect(coach.getByRole('heading', { level: 1, name: 'Yoklama' })).toBeVisible();
        await audit.screen(coach, 'take-attendance');

        await nav(coach, 'Gruplarım');
        await expect(coach.getByRole('link', { name: arranged.groupName })).toBeVisible();
        await audit.screen(coach, 'coach-groups');
        await coach.getByRole('link', { name: arranged.groupName }).click();
        await expect(coach.getByRole('heading', { level: 1 })).toBeVisible();
        await audit.screen(coach, 'coach-roster');
        await nav(coach, 'Duyurular');
        await expect(coach.getByRole('heading', { level: 1, name: 'Duyurular' })).toBeVisible();
        await audit.screen(coach, 'coach-announcements');
      } finally {
        await coachContext.close();
      }
    });
  } finally {
    await audit.writeReport(testInfo);
  }
});

/** Through the side menu (an in-app navigation keeps the in-memory session); opens the drawer on a phone. */
async function nav(page: Page, label: string): Promise<void> {
  const link = page.getByRole('link', { name: label, exact: true });
  const menu = page.getByRole('button', { name: 'Menüyü aç' });
  // Right after a viewport change the layout may still be switching between the side menu and the drawer: wait until
  // one of the two is there instead of deciding on a snapshot.
  await expect(link.or(menu).first()).toBeVisible();
  if (!(await link.isVisible())) {
    await menu.click();
  }
  await link.click();
}

async function expectFocusInside(page: Page, selector: string): Promise<void> {
  await expect
    .poll(() => page.evaluate((s) => !!document.activeElement?.closest(s), selector), {
      message: `focus moves into ${selector}`,
    })
    .toBe(true);
}

/** Every field marked invalid points at a visible error text through aria-describedby (3.3.1, 1.3.1). */
async function expectErrorsLinked(page: Page): Promise<void> {
  const unlinked = await page.evaluate(() =>
    Array.from(document.querySelectorAll('[aria-invalid="true"]'))
      .filter((field) => {
        const ids = (field.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
        return !ids.some((id) => document.getElementById(id)?.textContent?.trim());
      })
      .map((field) => field.id || field.getAttribute('name') || field.tagName),
  );
  expect(unlinked, 'invalid fields without a linked error message').toEqual([]);
}

/**
 * A group taught by a new coach whose only weekly slot is today (so the coach's "today" has a session), a monthly
 * fee from this month, two students enrolled from today with today's attendance and an announcement; the first
 * student's due is paid online through the Fake sandbox provider (a refundable payment), the second's is unpaid.
 */
async function arrange(api: ApiClient): Promise<Arranged> {
  const testInfo = test.info();
  const today = todayInSchool();
  const period = today.date.slice(0, 7);
  const sport = await api.post<{ id: string }>('/api/v1/sports', {
    name: unique('Tenis', testInfo),
  });
  const facility = await api.post<{ id: string }>('/api/v1/facilities', {
    name: unique('Tenis Kortu', testInfo),
    type: 'Court',
    address: { line: 'Deneme Cad. No:5', district: 'Tepebaşı', city: 'Eskişehir' },
    capacity: null,
  });
  const coachEmail = uniqueEmail('erisim-antrenor', testInfo);
  const coach = await api.post<{ coach: { id: string }; temporaryPassword: string }>(
    '/api/v1/coaches',
    {
      firstName: 'Ozan',
      lastName: unique('Erişim', testInfo),
      email: coachEmail,
      phone: null,
    },
  );
  const groupName = unique('U12 Tenis', testInfo);
  const group = await api.post<{ id: string; version: number }>('/api/v1/groups', {
    sportId: sport.id,
    name: groupName,
    birthYearRange: { from: 2013, to: 2016 },
    capacity: 12,
    facilityId: facility.id,
    coachId: coach.coach.id,
  });
  await api.put(`/api/v1/groups/${group.id}/schedule`, {
    slots: [{ day: today.day, start: '00:00', end: '23:59' }],
    version: group.version,
  });
  await api.post(`/api/v1/groups/${group.id}/fees`, {
    effectiveFrom: period,
    amount: { amount: fee, currency: 'TRY' },
  });

  const lastName = unique('Erişim', testInfo);
  const students: Arranged['students'] = [];
  const enrollments: string[] = [];
  for (const [i, firstName] of ['Ada', 'Can'].entries()) {
    const guardianPhone = phone(testInfo, 400 + i);
    const student = await api.post<{ id: string }>('/api/v1/students', {
      firstName,
      lastName,
      birthDate: '2014-04-01',
      gender: i === 0 ? 'Female' : 'Male',
      phone: null,
      email: null,
      emergencyContact: { name: `Veli ${lastName}`, phone: guardianPhone, relationship: 'Anne' },
      guardians: [
        {
          guardianId: null,
          newGuardian: {
            firstName: 'Veli',
            lastName,
            phone: guardianPhone,
            email: null,
            address: null,
          },
          relationship: 'Mother',
        },
      ],
    });
    // One at a time: enrolments take the student and group locks (IEnrollmentLocks).
    const enrollment = await api.post<{ id: string }>('/api/v1/enrollments', {
      studentId: student.id,
      groupId: group.id,
      startDate: today.date,
    });
    students.push({ id: student.id, name: `${firstName} ${lastName}` });
    enrollments.push(enrollment.id);
  }
  await api.put(`/api/v1/groups/${group.id}/sessions/${today.date}T00:00/attendance`, {
    records: [
      { studentId: students[0].id, status: 'Present' },
      { studentId: students[1].id, status: 'Absent' },
    ],
  });
  await api.post(`/api/v1/groups/${group.id}/announcements`, {
    title: unique('Kort bakımı', testInfo),
    body: 'Cumartesi antrenmanı kapalı salonda yapılacak.',
  });

  const attempt = await api.post<{ checkoutUrl: string }>(
    '/api/v1/online-payments',
    { enrollmentId: enrollments[0], period },
    201,
    { 'Idempotency-Key': randomUUID() },
  );
  const token = new URL(attempt.checkoutUrl, appUrl).searchParams.get('token');
  await api.post('/api/v1/sandbox-checkout/complete', { token, succeed: true }, 200);

  return { groupName, coachEmail, coachPassword: coach.temporaryPassword, students };
}
