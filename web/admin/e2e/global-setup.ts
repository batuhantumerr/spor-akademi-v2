import { randomBytes } from 'node:crypto';

import { ApiClient } from './support/api';
import { emailDomain, newPassword, schoolTimeZone } from './support/data';

interface SchoolCreated {
  school: { id: string };
  temporaryPassword: string;
}

/**
 * Seeds this run's school through the API, the way an operator would (US-01, US-05):
 * the Platform Admin that the API seeded at startup from the per-run E2E_PLATFORM_ADMIN_* settings
 * replaces its initial password, creates a school with its first School Admin, and the School Admin
 * replaces the temporary password. Credentials live only in this process's environment, which
 * Playwright passes on to the test workers; nothing is written to disk.
 */
export default async function globalSetup(): Promise<void> {
  const id = randomBytes(3).toString('hex');

  const initial = process.env['E2E_PLATFORM_ADMIN_PASSWORD']!;
  const platform = await ApiClient.login(process.env['E2E_PLATFORM_ADMIN_EMAIL']!, initial);
  await platform.changePassword(initial, newPassword());

  const adminEmail = `admin-${id}@${emailDomain}`;
  const created = await platform.post<SchoolCreated>('/api/v1/platform/schools', {
    name: `E2E Spor Okulu ${id}`,
    slug: `e2e-${id}`,
    timeZone: schoolTimeZone,
    admin: { firstName: 'Deniz', lastName: 'Test', email: adminEmail },
  });
  await platform.dispose();

  const adminPassword = newPassword();
  const admin = await ApiClient.login(adminEmail, created.temporaryPassword);
  await admin.changePassword(created.temporaryPassword, adminPassword);

  process.env['E2E_RUN_ID'] = id;
  process.env['E2E_ADMIN_EMAIL'] = adminEmail;
  process.env['E2E_ADMIN_PASSWORD'] = adminPassword;
  await admin.dispose();
}
