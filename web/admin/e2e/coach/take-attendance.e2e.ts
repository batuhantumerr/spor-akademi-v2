import { expect, test } from '@playwright/test';

import { expectNoSeriousA11yViolations } from '../support/a11y';
import { ApiClient } from '../support/api';
import {
  newPassword,
  phone,
  minutesToSchoolMidnight,
  schoolAdmin,
  todayInSchool,
  unique,
  uniqueEmail,
} from '../support/data';
import { signIn, toast } from '../support/ui';

/**
 * Coach takes attendance on a phone (P1-36, US-19, NFR-PRF-03, NFR-USA-01).
 *
 * The School Admin arranges a group through the API whose only weekly slot is today (school time
 * zone) from 00:00, so the session has always started, whatever the weekday and hour of the run
 * (attendance cannot be saved before a session starts, US-19). Twenty students are enrolled from today.
 */
const studentCount = 20;
const firstNames = [
  'Ali',
  'Ayşe',
  'Mehmet',
  'Zeynep',
  'Mustafa',
  'Elif',
  'Ahmet',
  'Defne',
  'Emir',
  'Ecrin',
  'Yusuf',
  'Asel',
  'Ömer',
  'Nehir',
  'Kerem',
  'Azra',
  'Eymen',
  'Lina',
  'Çınar',
  'İpek',
];

interface Arranged {
  groupId: string;
  groupName: string;
  sessionKey: string;
  coachEmail: string;
  coachPassword: string;
  students: { id: string; firstName: string; lastName: string }[];
}

let arranged: Arranged;
/** One fresh School Admin login per project, kept for the read-back (logins are rate limited). */
let admin: ApiClient | undefined;

test.beforeAll(async () => {
  const testInfo = test.info();
  // "Today" must not change between arranging the session and saving its attendance.
  const toMidnight = minutesToSchoolMidnight();
  test.skip(
    toMidnight < 5 || toMidnight > 24 * 60 - 5,
    'Skipped within 5 minutes of midnight in Europe/Istanbul: the session date would change mid-test.',
  );
  const credentials = schoolAdmin();
  admin = await ApiClient.login(credentials.email, credentials.password);
  const today = todayInSchool();
  const sport = await admin.post<{ id: string }>('/api/v1/sports', {
    name: unique('Basketbol', testInfo),
  });
  const facility = await admin.post<{ id: string }>('/api/v1/facilities', {
    name: unique('Spor Salonu', testInfo),
    type: 'Court',
    address: { line: 'Deneme Cad. No:2', district: 'Bornova', city: 'İzmir' },
    capacity: null,
  });
  const coachEmail = uniqueEmail('saha-antrenoru', testInfo);
  const coach = await admin.post<{ coach: { id: string }; temporaryPassword: string }>(
    '/api/v1/coaches',
    { firstName: 'Can', lastName: unique('Koç', testInfo), email: coachEmail, phone: null },
  );
  const groupName = unique('U10 Basketbol', testInfo);
  const group = await admin.post<{ id: string; version: number }>('/api/v1/groups', {
    sportId: sport.id,
    name: groupName,
    birthYearRange: { from: 2014, to: 2016 },
    capacity: 25,
    facilityId: facility.id,
    coachId: coach.coach.id,
  });
  await admin.put(`/api/v1/groups/${group.id}/schedule`, {
    slots: [{ day: today.day, start: '00:00', end: '23:59' }],
    version: group.version,
  });

  const lastName = unique('Yılmaz', testInfo);
  const students: Arranged['students'] = [];
  for (let i = 0; i < studentCount; i++) {
    const guardianPhone = phone(testInfo, 100 + i);
    const student = await admin.post<{ id: string }>('/api/v1/students', {
      firstName: firstNames[i],
      lastName,
      birthDate: '2015-05-01',
      gender: i % 2 === 0 ? 'Male' : 'Female',
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
    await admin.post('/api/v1/enrollments', {
      studentId: student.id,
      groupId: group.id,
      startDate: today.date,
    });
    students.push({ id: student.id, firstName: firstNames[i], lastName });
  }

  arranged = {
    groupId: group.id,
    groupName,
    sessionKey: `${today.date}T00:00`,
    coachEmail,
    coachPassword: coach.temporaryPassword,
    students,
  };
});

test('coach signs in on a phone and records attendance for 20 students', async ({
  page,
}, testInfo) => {
  // First sign-in with the temporary password leads to the password change (US-05).
  await page.goto('/login');
  await signIn(page, arranged.coachEmail, arranged.coachPassword);
  await expect(page).toHaveURL(/\/change-password$/);
  const password = newPassword();
  await page.getByLabel('Mevcut şifre', { exact: true }).fill(arranged.coachPassword);
  await page.getByLabel('Yeni şifre', { exact: true }).fill(password);
  await page.getByLabel('Yeni şifre (tekrar)', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Şifreyi değiştir' }).click();
  await expect(page).toHaveURL(/\/coach\/today$/);

  const open = page.getByRole('link', { name: `Yoklama al: ${arranged.groupName} 00:00` });
  await expect(open).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'coach-today');

  // NFR-PRF-03: from today's list to the saved attendance. The target (< 30 s) is for a person;
  // the automated run must stay far below it, so the bound only catches a broken or stalled flow.
  const started = Date.now();
  await open.click();
  await expect(page.getByRole('heading', { name: 'Yoklama' })).toBeVisible();

  const absent = new Set([3, 11]);
  const late = new Set([7]);
  for (const [i, student] of arranged.students.entries()) {
    const row = page.getByRole('group', {
      name: `${student.firstName} ${student.lastName} için yoklama durumu`,
    });
    const status = absent.has(i) ? 'Gelmedi' : late.has(i) ? 'Geç' : 'Geldi';
    await row.getByText(status, { exact: true }).click();
  }
  await expect(page.getByText(`${studentCount} / ${studentCount} işaretlendi`)).toBeVisible();
  await page.getByRole('button', { name: 'Yoklamayı kaydet' }).click();
  await expect(toast(page, 'Yoklama kaydedildi.')).toBeVisible();
  const elapsed = Date.now() - started;

  testInfo.annotations.push({
    type: 'NFR-PRF-03',
    description: `${elapsed} ms for ${studentCount} students`,
  });
  console.log(`[${testInfo.project.name}] NFR-PRF-03 attendance flow: ${elapsed} ms`);
  expect(elapsed, 'open today and save 20 students (target < 30 s)').toBeLessThan(30_000);

  await expect(
    page.getByText('Bu antrenmanın yoklaması daha önce alınmış; değişiklik yapabilirsiniz.'),
  ).toBeVisible();
  await expectNoSeriousA11yViolations(page, testInfo, 'take-attendance');

  // What the server stored, read back as the School Admin.
  const saved = await admin!.get<{ students: { studentId: string; status: string | null }[] }>(
    `/api/v1/groups/${arranged.groupId}/sessions/${arranged.sessionKey}/attendance`,
  );
  const counts = saved.students.reduce<Record<string, number>>((acc, s) => {
    acc[s.status ?? 'none'] = (acc[s.status ?? 'none'] ?? 0) + 1;
    return acc;
  }, {});
  expect(counts).toEqual({ Present: 17, Absent: 2, Late: 1 });
});

test.afterAll(async () => {
  await admin?.dispose();
});
