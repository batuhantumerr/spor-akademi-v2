import { BrowserContext, Page, expect, test } from '@playwright/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { phone, schoolAdmin, unique, uniqueEmail } from '../support/data';
import { dialog, scriptReadableState, signIn, toast } from '../support/ui';

/**
 * School Admin critical flows (P1-36) in one browser session: sign in, survive a reload, set up a group
 * with its weekly schedule, register a student with a guardian, enrol the student, sign out.
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
