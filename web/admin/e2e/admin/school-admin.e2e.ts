import { BrowserContext, Page, expect, test } from '@playwright/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { ApiClient } from '../support/api';
import {
  minutesToSchoolMidnight,
  phone,
  schoolAdmin,
  todayInSchool,
  unique,
  uniqueEmail,
} from '../support/data';
import {
  answerRefreshWithoutSession,
  dialog,
  scriptReadableState,
  signIn,
  toast,
} from '../support/ui';

/**
 * School Admin critical flows (P1-36) in one browser session: sign in, survive a reload, set up a group
 * with its weekly schedule, register a student with a guardian, enrol the student, sign out.
 *
 * Phase 2 exit criteria (docs/faz-2-cikis-kriterleri.md), in the same session before signing out:
 * - P2-F1: a due is paid end to end through the sandbox provider. The School Admin starts an online payment
 *   for an unpaid due and shares the link (DEC-348); the payer opens it in a browser of their own (no
 *   session), pays on the Fake provider's hosted page (DEC-347) and lands on the result page. The payment
 *   is recorded once, as Online, and the due is gone.
 * - P2-F8: a fee rules version with a sibling discount shows on the dues list. Fee rules are school-wide and
 *   start at most once per month (DEC-370): the first browser project of a run defines them through the
 *   settings page, later ones find them in place.
 * Their data is arranged through the API with this session's access token (no extra login, DEC-241).
 *
 * One page is shared by the serial steps on purpose: the access token lives only in memory, so a fresh
 * page per test would mean a fresh login (rate limited, NFR-SEC-14) without testing anything more.
 */
test.describe.configure({ mode: 'serial' });

let context: BrowserContext;
let page: Page;
let accessToken: string;

const names = {
  sport: '',
  facility: '',
  coachFirst: 'Ece',
  coachLast: '',
  group: '',
  studentFirst: 'Elif',
  studentLast: '',
  guardianFirst: 'Ayşe',
};

/** Monthly fee of the groups the dues tests arrange. */
const fee = '750.00';
/** Arranges the dues tests' data; created on first use from the session's access token. */
let api: ApiClient | undefined;
/** This month in the school's time zone (YYYY-MM): the period of the dues under test. */
let period: string;
let duesSetup: { sport: string; facility: string } | undefined;

interface ArrangedDues {
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

test.beforeAll(async ({ browser }, testInfo) => {
  // Playwright Test applies the project's `use` options (baseURL, device, locale) to this context too.
  context = await browser.newContext();
  page = await context.newPage();
  names.sport = unique('Futbol', testInfo);
  names.facility = unique('Merkez Saha', testInfo);
  names.coachLast = unique('Antrenör', testInfo);
  names.group = unique('U10 Futbol', testInfo);
  names.studentLast = unique('Öğrenci', testInfo);
});

test.afterAll(async () => {
  await api?.dispose();
  await context?.close();
});

test('signs in; the access token stays in memory and the refresh token in an HttpOnly cookie', async () => {
  const testInfo = test.info();
  const admin = schoolAdmin();
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'login');

  const session = await signIn(page, admin.email, admin.password);
  accessToken = session.accessToken;
  // DEC-053, from the response itself (WebKit on Windows does not report SameSite in its cookie jar).
  const cookie = session.setCookie.toLowerCase();
  expect(cookie).toMatch(/^sa_refresh=/);
  for (const attribute of ['httponly', 'secure', 'samesite=strict', 'path=/api/v1/auth']) {
    expect(cookie).toContain(attribute);
  }

  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toBeVisible();
  await expectTokenOnlyInMemory();
});

test('a reload restores the session through the refresh cookie', async () => {
  const refreshed = page.waitForResponse(
    (r) => r.url().endsWith('/api/v1/auth/refresh') && r.request().method() === 'POST',
  );
  await page.reload();
  expect((await refreshed).status()).toBe(200);

  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toBeVisible();
  await expectTokenOnlyInMemory();
});

test('sets up a sport, a facility and a coach', async () => {
  const testInfo = test.info();
  await page.getByRole('link', { name: 'Branşlar' }).click();
  await page.getByLabel('Branş adı', { exact: true }).fill(names.sport);
  await page.getByRole('button', { name: 'Branş ekle' }).click();
  await expect(toast(page, 'Branş eklendi.')).toBeVisible();
  await expect(page.getByRole('cell', { name: names.sport, exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Tesisler' }).click();
  await page.getByRole('button', { name: 'Tesis ekle' }).click();
  const facility = dialog(page);
  await facility.getByLabel('Tesis adı', { exact: true }).fill(names.facility);
  await facility.getByLabel('Tür', { exact: true }).selectOption({ label: 'Saha' });
  await facility.getByLabel('Adres', { exact: true }).fill('Deneme Sok. No:1');
  await facility.getByLabel('İlçe', { exact: true }).fill('Kadıköy');
  await facility.getByLabel('İl', { exact: true }).fill('İstanbul');
  await facility.getByRole('button', { name: 'Kaydet' }).click();
  await expect(toast(page, 'Tesis kaydedildi.')).toBeVisible();
  await expect(page.getByText(names.facility, { exact: true })).toBeVisible();

  await page.getByRole('link', { name: 'Antrenörler' }).click();
  await page.getByRole('button', { name: 'Antrenör ekle' }).click();
  const coach = dialog(page);
  await coach.getByLabel('Ad', { exact: true }).fill(names.coachFirst);
  await coach.getByLabel('Soyad', { exact: true }).fill(names.coachLast);
  await coach.getByLabel('E-posta', { exact: true }).fill(uniqueEmail('antrenor', testInfo));
  await coach.getByRole('button', { name: 'Antrenör ekle' }).click();
  // The one-time temporary password is shown once (US-08); the coach is not used to sign in here.
  await expect(page.getByRole('heading', { name: 'Geçici şifre' })).toBeVisible();
  await page.getByRole('button', { name: 'Not aldım, kapat' }).click();
  await expect(toast(page, 'Antrenör eklendi.')).toBeVisible();
  await expect(
    page.getByText(`${names.coachFirst} ${names.coachLast}`, { exact: true }),
  ).toBeVisible();
});

test('creates a group with capacity and a weekly schedule', async () => {
  const testInfo = test.info();
  await page.getByRole('link', { name: 'Gruplar' }).click();
  await page.getByRole('button', { name: 'Grup ekle' }).click();
  const form = dialog(page);
  await form.getByLabel('Grup adı', { exact: true }).fill(names.group);
  await form.getByLabel('Branş', { exact: true }).selectOption({ label: names.sport });
  await form.getByLabel('Doğum yılı (başlangıç)', { exact: true }).fill('2014');
  await form.getByLabel('Doğum yılı (bitiş)', { exact: true }).fill('2016');
  await form.getByLabel('Kontenjan', { exact: true }).fill('18');
  await form.getByLabel('Tesis', { exact: true }).selectOption({ label: names.facility });
  await form
    .getByLabel('Antrenör', { exact: true })
    .selectOption({ label: `${names.coachFirst} ${names.coachLast}` });
  await form.getByRole('button', { name: 'Kaydet' }).click();
  await expect(toast(page, 'Grup kaydedildi.')).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'groups');

  await page.getByRole('link', { name: names.group }).click();
  await expect(page.getByRole('heading', { name: names.group })).toBeVisible();
  await page.getByRole('button', { name: 'Saat ekle' }).click();
  await page.getByLabel('Gün', { exact: true }).selectOption({ label: 'Salı' });
  await page.getByLabel('Başlangıç', { exact: true }).fill('17:00');
  await page.getByLabel('Bitiş', { exact: true }).fill('18:30');
  await page.getByRole('button', { name: 'Programı kaydet' }).click();
  await expect(toast(page, 'Program kaydedildi.')).toBeVisible();
  // The next 14 days always contain two Tuesdays.
  await expect(page.getByText(/17:00\s*[–-]\s*18:30/).first()).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'group-detail');
});

test('registers a student with a guardian and enrols the student in the group', async () => {
  const testInfo = test.info();
  await page.getByRole('link', { name: 'Öğrenciler' }).click();
  await page.getByRole('link', { name: 'Öğrenci kaydet' }).click();
  await expect(page.getByRole('heading', { name: 'Öğrenci kaydı' })).toBeVisible();

  await page.locator('#s-firstName').fill(names.studentFirst);
  await page.locator('#s-lastName').fill(names.studentLast);
  await page.locator('#s-birthDate').fill('2015-05-01');
  await page.locator('#s-gender').selectOption('Female');

  await page.getByRole('button', { name: 'Veli ekle' }).click();
  const guardian = page.getByRole('group', { name: '1. veli' });
  const guardianPhone = phone(testInfo, 1);
  await guardian.getByLabel('Velinin telefonu', { exact: true }).fill(guardianPhone);
  await guardian.getByRole('button', { name: 'Telefonla ara' }).click();
  await expect(
    guardian.getByText('Bu numarayla kayıtlı veli yok.', { exact: false }),
  ).toBeVisible();
  await guardian.getByLabel('Ad', { exact: true }).fill(names.guardianFirst);
  await guardian.getByLabel('Soyad', { exact: true }).fill(names.studentLast);
  await expect(guardian.getByLabel('Telefon', { exact: true })).not.toHaveValue('');
  await guardian.getByLabel('Yakınlık', { exact: true }).selectOption({ label: 'Anne' });

  await page.getByRole('button', { name: /^Veliden kopyala/ }).click();
  await expectNoSeriousA11yViolations(page, testInfo, 'register-student');
  await page.getByRole('button', { name: 'Öğrenciyi kaydet' }).click();
  await expect(toast(page, 'Öğrenci kaydedildi.')).toBeVisible();
  await expect(page).toHaveURL(/\/students\/[0-9a-f-]{36}$/);
  await expect(page.getByText(`${names.guardianFirst} ${names.studentLast}`).first()).toBeVisible();

  await page.getByRole('button', { name: 'Gruba kaydet' }).click();
  const enroll = dialog(page);
  await enroll.getByLabel('Grup', { exact: true }).selectOption({ label: names.group });
  await expect(enroll.getByText(/Kontenjan 0 \/ 18/)).toBeVisible();
  await enroll.getByRole('button', { name: 'Gruba kaydet' }).click();
  await expect(toast(page, 'Öğrenci gruba kaydedildi.')).toBeVisible();
  await expect(page.getByRole('link', { name: names.group })).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'student-detail');
});

test('a due is paid online through the sandbox provider and recorded once', async ({ browser }) => {
  const testInfo = test.info();
  const arranged = await arrangeDues('U12 Voleybol', ['Deniz'], 1);
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

  const recorded = await api!.get<{ items: PaymentRow[] }>(
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
  const arranged = await arrangeDues('U12 Kardeş', ['Ada', 'Arda'], 2);
  const [first, second] = arranged.students;

  // #79: the previous test left the dues page open, filtered to another group. Its menu link rebuilds the page:
  // the filter is cleared and the group and students arranged just now are listed.
  await expect(page).toHaveURL(/\/dues(\?|$)/);
  await nav('Ödenmemiş borçlar');
  await expect(page.getByLabel('Grup', { exact: true })).toHaveValue('');
  await page.getByLabel('Grup', { exact: true }).selectOption({ label: arranged.groupName });
  await expect(page.getByRole('row', { name: new RegExp(second.name) })).toHaveCount(1);

  const versions = await api!.get<{ effectiveFrom: string; secondChildPercent: number }[]>(
    '/api/v1/settings/billing-rules',
  );
  const inPlace = versions.find((v) => v.effectiveFrom === period);
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

test('signs out; the refresh cookie is gone and a reload stays signed out', async () => {
  await page.getByRole('button', { name: 'Çıkış yap' }).click();
  await expect(page).toHaveURL(/\/login$/);
  expect((await context.cookies()).find((c) => c.name === 'sa_refresh')).toBeUndefined();

  await page.reload();
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('heading', { name: 'Giriş yap' })).toBeVisible();
});

/** NFR-SEC-06: scripts can read neither token; the browser holds the refresh token HttpOnly. */
async function expectTokenOnlyInMemory(): Promise<void> {
  const readable = (await scriptReadableState(page)).join('\n');
  expect(readable).not.toContain(accessToken);
  expect(readable, 'no JWT in Web Storage or document.cookie').not.toMatch(/eyJ[\w-]+\.[\w-]+\./);
  expect(readable).not.toContain('sa_refresh');

  const refresh = (await context.cookies()).find((c) => c.name === 'sa_refresh');
  expect(refresh, 'refresh cookie').toBeDefined();
  expect(refresh!.httpOnly).toBe(true);
  expect(refresh!.secure).toBe(true);
  expect(refresh!.path).toBe('/api/v1/auth');
}

/** Navigates through the side menu (an in-app navigation keeps the in-memory session). */
async function nav(label: string): Promise<void> {
  await page.getByRole('link', { name: label, exact: true }).click();
}

/**
 * A group with a monthly fee from this month and students enrolled from today, all sharing one new
 * guardian (phone unique per project and `sequence`). Skips near midnight in the school's time zone, where
 * the period of the due could change mid-test.
 */
async function arrangeDues(
  label: string,
  firstNames: string[],
  sequence: number,
): Promise<ArrangedDues> {
  const testInfo = test.info();
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 5 || toMidnight > 24 * 60 - 5,
    'Skipped within 5 minutes of midnight in Europe/Istanbul: the period of the due could change mid-test.',
  );
  period = todayInSchool().date.slice(0, 7);
  api ??= await ApiClient.withToken(accessToken);
  duesSetup ??= {
    sport: (
      await api.post<{ id: string }>('/api/v1/sports', { name: unique('Voleybol', testInfo) })
    ).id,
    facility: (
      await api.post<{ id: string }>('/api/v1/facilities', {
        name: unique('Aidat Salonu', testInfo),
        type: 'Court',
        address: { line: 'Deneme Cad. No:3', district: 'Çankaya', city: 'Ankara' },
        capacity: null,
      })
    ).id,
  };

  const groupName = unique(label, testInfo);
  const group = await api.post<{ id: string }>('/api/v1/groups', {
    sportId: duesSetup.sport,
    name: groupName,
    birthYearRange: { from: 2013, to: 2016 },
    capacity: 20,
    facilityId: duesSetup.facility,
    coachId: null,
  });
  await api.post(`/api/v1/groups/${group.id}/fees`, {
    effectiveFrom: period,
    amount: { amount: fee, currency: 'TRY' },
  });

  const lastName = unique('Aidat', testInfo);
  const guardianPhone = phone(testInfo, 200 + sequence);
  const today = todayInSchool().date;
  const students: ArrangedDues['students'] = [];
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
    // One at a time: enrolments take the student and group locks (IEnrollmentLocks).
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
