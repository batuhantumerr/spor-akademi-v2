import { BrowserContext, Page, expect, test } from '@playwright/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { ApiClient } from '../support/api';
import {
  minutesToSchoolMidnight,
  phone,
  schoolAdmin,
  todayInSchool,
  unique,
} from '../support/data';
import { answerRefreshWithoutSession, signIn, toast } from '../support/ui';

/**
 * Phase 2 exit criteria (docs/faz-2-cikis-kriterleri.md):
 * - P2-F1: a due is paid end to end through the sandbox provider. The School Admin starts an online payment for an
 *   unpaid due and shares the link (DEC-348); the payer opens it in a browser of their own (no session), pays on the
 *   Fake provider's hosted page (DEC-347) and lands on the result page. The payment is recorded once, as Online, and
 *   the due is gone.
 * - P2-F8: a fee rules version with a sibling discount shows on the dues list.
 *
 * The School Admin signs in once through the form; the data is arranged through the API with that session's access
 * token (no second login, NFR-SEC-14). Fee rules are school-wide and start at most once per month (DEC-370), so the
 * first browser project of a run defines them through the settings page and later ones find them in place.
 */
test.describe.configure({ mode: 'serial' });

const fee = '750.00';

let context: BrowserContext;
let page: Page;
let api: ApiClient;
let period: string;
let sport: string;
let facility: string;

interface Arranged {
  groupId: string;
  groupName: string;
  students: { id: string; name: string; enrollmentId: string }[];
}

interface PaymentRow {
  enrollmentId: string;
  method: string;
  status: string;
  amount: { amount: string };
}

test.beforeAll(async ({ browser }) => {
  const testInfo = test.info();
  // The due belongs to this month in the school's time zone; it must not change while the test runs.
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 5 || toMidnight > 24 * 60 - 5,
    'Skipped within 5 minutes of midnight in Europe/Istanbul: the period of the due could change mid-test.',
  );
  period = todayInSchool().date.slice(0, 7);

  context = await browser.newContext();
  page = await context.newPage();
  // No refresh cookie yet: the start-up restore is answered in the browser (the session tests need the budget).
  const removeRoute = await answerRefreshWithoutSession(context);
  await page.goto('/login');
  const admin = schoolAdmin();
  const session = await signIn(page, admin.email, admin.password);
  await expect(page).toHaveURL(/\/dashboard$/);
  await removeRoute();
  api = await ApiClient.withToken(session.accessToken);

  sport = (await api.post<{ id: string }>('/api/v1/sports', { name: unique('Voleybol', testInfo) }))
    .id;
  facility = (
    await api.post<{ id: string }>('/api/v1/facilities', {
      name: unique('Aidat Salonu', testInfo),
      type: 'Court',
      address: { line: 'Deneme Cad. No:3', district: 'Çankaya', city: 'Ankara' },
      capacity: null,
    })
  ).id;
});

test.afterAll(async () => {
  await api?.dispose();
  await context?.close();
});

test('a due is paid online through the sandbox provider and recorded once', async ({ browser }) => {
  const testInfo = test.info();
  const arranged = await arrange('U12 Voleybol', ['Deniz'], 1);
  const [student] = arranged.students;

  // The School Admin starts the online payment from the dues list.
  await nav('Ödenmemiş borçlar');
  await page.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
  await expect(page.getByRole('row', { name: new RegExp(student.name) })).toHaveCount(1);
  await page
    .getByRole('button', { name: new RegExp(`^Online ödeme bağlantısı: ${student.name},`) })
    .click();
  await expect(
    toast(page, 'Ödeme bağlantısı oluşturuldu. Bağlantıyı kopyalayıp ödeyecek kişiyle paylaşın.'),
  ).toBeVisible();
  await expect(page).toHaveURL(/\/online-payments\?/);
  const attempt = page.getByRole('row', { name: new RegExp(student.name) });
  await expect(attempt.getByText('Ödeme bekleniyor', { exact: true })).toBeVisible();
  const link = await attempt.getByRole('link', { name: 'Ödeme sayfasını aç' }).getAttribute('href');
  expect(link, 'shareable checkout link').toMatch(
    /^https:\/\/localhost:\d+\/sandbox-odeme\?token=/,
  );

  // The payer opens the shared link in a browser of their own and pays on the sandbox page.
  const payer = await browser.newContext();
  try {
    await answerRefreshWithoutSession(payer);
    const checkout = await payer.newPage();
    await checkout.goto(link!);
    await expect(checkout.getByRole('heading', { name: 'Sandbox ödeme sayfası' })).toBeVisible();
    await expectNoSeriousA11yViolations(checkout, testInfo, 'sandbox-checkout');
    await checkout.getByRole('button', { name: 'Ödemeyi onayla' }).click();
    await expect(checkout).toHaveURL(/\/odeme-sonucu\?durum=Succeeded$/);
    await expect(checkout.getByTestId('result-message')).toHaveText(
      'Ödemeniz alındı. Teşekkür ederiz.',
    );
  } finally {
    await payer.close();
  }

  // Back in the admin panel: the payment is listed once as Online, the attempt is paid, the due is gone.
  await nav('Ödemeler');
  await page.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
  const payments = page.getByRole('row', { name: new RegExp(student.name) });
  await expect(payments).toHaveCount(1);
  await expect(payments.getByRole('cell', { name: 'Online', exact: true })).toBeVisible();

  // A fresh visit (the attempts page above was opened with the due's filter).
  await nav('Online ödemeler');
  const paid = page.getByRole('row', { name: new RegExp(student.name) });
  await expect(paid).toHaveCount(1);
  await expect(paid.getByText('Ödendi', { exact: true })).toBeVisible();
  await expect(paid.getByRole('link', { name: 'Ödemeyi gör' })).toBeVisible();

  const recorded = await api.get<{ items: PaymentRow[] }>(
    `/api/v1/payments?groupId=${arranged.groupId}&pageSize=100`,
  );
  expect(recorded.items.map((p) => [p.enrollmentId, p.method, p.status, p.amount.amount])).toEqual([
    [student.enrollmentId, 'Online', 'Recorded', fee],
  ]);

  await nav('Ödenmemiş borçlar');
  await page.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
  await expect(page.getByText('Bu filtrelerde ödenmemiş borç yok.', { exact: true })).toBeVisible();
});

test('a sibling discount defined under fee rules shows on the dues list', async () => {
  const testInfo = test.info();
  // Two students linked through the same guardian, in one group: the second one gets the discount.
  const arranged = await arrange('U12 Kardeş', ['Ada', 'Arda'], 2);
  const [first, second] = arranged.students;

  const versions = await api.get<{ effectiveFrom: string; secondChildPercent: number }[]>(
    '/api/v1/settings/billing-rules',
  );
  const inPlace = versions.find((v) => v.effectiveFrom === period);
  // Also leaves the dues page of the previous test: the menu link of the current page would not reload it.
  await nav('Aidat kuralları');
  await expect(page.getByRole('heading', { name: 'Aidat kuralları' })).toBeVisible();
  if (inPlace) {
    testInfo.annotations.push({
      type: 'note',
      description: `Fee rules for ${period} were defined by an earlier browser project of this run.`,
    });
    await expect(page.getByText('Güncel', { exact: true })).toBeVisible();
  } else {
    // DEC-376: next month is suggested; this month changes this month's open dues and asks first.
    await page.getByLabel('Geçerlilik başlangıcı', { exact: true }).fill(period);
    await expect(page.getByLabel('2. kardeş indirimi (%)', { exact: true })).toHaveValue('10');
    await expectNoSeriousA11yViolations(page, testInfo, 'billing-rules');
    await page.getByRole('button', { name: 'Kuralları kaydet' }).click();
    await page
      .getByRole('alertdialog')
      .getByRole('button', { name: 'Bu aydan itibaren kaydet' })
      .click();
    await expect(toast(page, 'Aidat kuralları kaydedildi.')).toBeVisible();
  }
  const percent = inPlace ? Number(inPlace.secondChildPercent) : 10;

  await nav('Ödenmemiş borçlar');
  await page.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
  const full = page.getByRole('row', { name: new RegExp(first.name) });
  const discounted = page.getByRole('row', { name: new RegExp(second.name) });
  await expect(discounted.getByTestId('discount-hint')).toHaveText(`Kardeş indirimi %${percent}`);
  await expect(discounted.getByTestId('list-fee')).toBeVisible();
  await expect(full.getByTestId('discount-hint')).toHaveCount(0);
  await expectNoSeriousA11yViolations(page, testInfo, 'dues-sibling-discount');
});

/** Navigates through the side menu (an in-app navigation keeps the in-memory session). */
async function nav(label: string): Promise<void> {
  await page.getByRole('link', { name: label, exact: true }).click();
}

/**
 * A group with a monthly fee from this month and students enrolled from today, all sharing one new guardian; the
 * guardian's phone is unique per project and `sequence`.
 */
async function arrange(label: string, firstNames: string[], sequence: number): Promise<Arranged> {
  const testInfo = test.info();
  const groupName = unique(label, testInfo);
  const group = await api.post<{ id: string }>('/api/v1/groups', {
    sportId: sport,
    name: groupName,
    birthYearRange: { from: 2013, to: 2016 },
    capacity: 20,
    facilityId: facility,
    coachId: null,
  });
  await api.post(`/api/v1/groups/${group.id}/fees`, {
    effectiveFrom: period,
    amount: { amount: fee, currency: 'TRY' },
  });

  const lastName = unique('Aidat', testInfo);
  const guardianPhone = phone(testInfo, 200 + sequence);
  const today = todayInSchool().date;
  const students: Arranged['students'] = [];
  let guardianId: string | null = null;
  for (const firstName of firstNames) {
    const student: { id: string; guardians: { guardianId: string }[] } = await api.post(
      '/api/v1/students',
      {
        firstName,
        lastName,
        birthDate: '2014-03-01',
        gender: 'Female',
        phone: null,
        email: null,
        emergencyContact: { name: `Veli ${lastName}`, phone: guardianPhone, relationship: 'Anne' },
        guardians: [
          guardianId
            ? { guardianId, newGuardian: null, relationship: 'Mother' }
            : {
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
      },
    );
    guardianId ??= student.guardians[0].guardianId;
    const enrollment = await api.post<{ id: string }>('/api/v1/enrollments', {
      studentId: student.id,
      groupId: group.id,
      startDate: today,
    });
    students.push({
      id: student.id,
      name: `${firstName} ${lastName}`,
      enrollmentId: enrollment.id,
    });
  }
  return { groupId: group.id, groupName, students };
}
