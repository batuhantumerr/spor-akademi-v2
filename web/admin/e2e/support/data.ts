import { randomBytes } from 'node:crypto';

import { TestInfo } from '@playwright/test';

/** Synthetic data only: reserved .test e-mail domain, phones that do not look like real lines. */
export const schoolTimeZone = 'Europe/Istanbul';
export const emailDomain = 'e2e.test';

/** Set by the global setup; distinguishes this run's records in logs and screenshots. */
export function runId(): string {
  return required('E2E_RUN_ID');
}

export function schoolAdmin(): { email: string; password: string; token: string } {
  return {
    email: required('E2E_ADMIN_EMAIL'),
    password: required('E2E_ADMIN_PASSWORD'),
    token: required('E2E_ADMIN_TOKEN'),
  };
}

/** A password for this run only: long, random, never written anywhere. */
export function newPassword(): string {
  return `E2e-${randomBytes(18).toString('hex')}`;
}

/** Name unique per run and browser project, so projects never collide in the shared school. */
export function unique(label: string, testInfo: TestInfo): string {
  return `${label} ${runId()} ${testInfo.project.name}`;
}

export function uniqueEmail(local: string, testInfo: TestInfo): string {
  return `${local}-${runId()}-${testInfo.project.name}@${emailDomain}`;
}

const projectOrder = ['chromium', 'firefox', 'webkit', 'mobile-chromium', 'mobile-webkit'];

/**
 * A valid Turkish mobile number of the form `+90 5pn 000 nnnn` (like the demo seed), unique per project
 * `p` and sequence `n` within the run's school.
 */
export function phone(testInfo: TestInfo, sequence: number): string {
  const project = Math.max(0, projectOrder.indexOf(testInfo.project.name));
  const block = Math.floor(sequence / 10_000) % 10;
  return `+90 5${project + 3}${block} 000 ${String(sequence % 10_000).padStart(4, '0')}`;
}

/** Today's date and weekday in the school's time zone (the API decides "today" there too). */
export function todayInSchool(now = new Date()): { date: string; day: string } {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: schoolTimeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    weekday: 'long',
  }).formatToParts(now);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)!.value;
  return { date: `${part('year')}-${part('month')}-${part('day')}`, day: part('weekday') };
}

function required(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is not set; the global setup did not run (use bash scripts/e2e.sh).`);
  }
  return value;
}
