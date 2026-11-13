import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AuditEntryDto } from '../../api/models';
import { AuthService, CurrentUser } from '../../core/auth/auth.service';
import { ui } from '../../core/i18n/tr';
import { AUDIT_PAGE_SIZE, AuditLogPage } from './audit-log.page';

const admin: CurrentUser = {
  id: 'u1',
  email: 'admin@example.test',
  firstName: 'Ece',
  lastName: 'Demir',
  role: 'SchoolAdmin',
  tenantId: 't1',
  mustChangePassword: false,
  schoolTimeZone: 'Europe/Istanbul',
};

const update: AuditEntryDto = {
  id: 'a1',
  occurredAt: '2027-01-14T09:30:00Z',
  userId: 'u1',
  userName: 'Ece Demir',
  userRole: 'SchoolAdmin',
  action: 'Update',
  entityType: 'Student',
  entityId: '01a1d44c-a1ad-746e-804a-5aeca2d62f49',
  fields: ['FirstName', 'Email'],
  reason: null,
  correlationId: 'c1',
};

const anonymized: AuditEntryDto = {
  ...update,
  id: 'a2',
  userId: null,
  userName: null,
  userRole: null,
  action: 'Erase',
  reason: 'DataSubjectRequest',
  fields: ['BirthDate'],
};

const list: AuditEntryDto = {
  ...update,
  id: 'a3',
  action: 'List',
  entityId: null,
  fields: ['search'],
};

const pageOf = (items: AuditEntryDto[]) => ({
  items,
  page: 1,
  pageSize: AUDIT_PAGE_SIZE,
  totalCount: items.length,
  totalPages: items.length === 0 ? 0 : 1,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('AuditLogPage', () => {
  let fixture: ComponentFixture<AuditLogPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [AuditLogPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { user: signal(admin) } },
      ],
    });
    fixture = TestBed.createComponent(AuditLogPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    backend
      .expectOne('/api/v1/audit-log/actors')
      .flush([{ userId: 'u2', userName: 'Deniz Kaya', userRole: 'Coach' }]);
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const entries = () => backend.match((r) => r.url === '/api/v1/audit-log');
  const control = (id: string) => root.querySelector(`#${id}`) as HTMLInputElement;
  async function change(id: string, value: string): Promise<void> {
    control(id).value = value;
    control(id).dispatchEvent(new Event('change'));
    await settle(fixture);
  }

  it('shows who did what to which record and the field names, never values', async () => {
    const [first] = entries();
    expect(first.request.params.get('pageSize')).toBe(String(AUDIT_PAGE_SIZE));
    expect(first.request.params.has('entityType')).toBe(false);
    first.flush(pageOf([update, anonymized, list]));
    await settle(fixture);

    const rows = [...root.querySelectorAll('tbody tr')].map((r) => r.textContent ?? '');
    expect(rows[0]).toContain('Ece Demir');
    expect(rows[0]).toContain(ui.roles['SchoolAdmin']);
    expect(rows[0]).toContain(ui.auditLog.actions['Update']);
    expect(rows[0]).toContain('Ad, E-posta');
    expect(rows[0]).toContain('6a2d62f49'.slice(-8));
    // School time (Istanbul, UTC+3).
    expect(rows[0]).toContain('12:30');
    expect(rows[1]).toContain(ui.auditLog.system);
    expect(rows[1]).toContain(ui.auditLog.reasons['DataSubjectRequest']);
    expect(rows[2]).toContain(ui.auditLog.listQuery);
    const link = root.querySelector('tbody a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/students/01a1d44c-a1ad-746e-804a-5aeca2d62f49');
    // WCAG 2.5.3 Label in Name: the accessible name starts with the visible short id; no aria-label replaces it.
    expect(link.hasAttribute('aria-label')).toBe(false);
    expect(link.textContent?.trim()).toBe(`a2d62f49 ${ui.auditLog.openStudent}`);
  });

  it('filters by record type, user and date range', async () => {
    entries()[0].flush(pageOf([update]));
    await settle(fixture);

    await change('audit-type', 'Guardian');
    const byType = entries()[0];
    expect(byType.request.params.get('entityType')).toBe('Guardian');
    byType.flush(pageOf([]));
    await settle(fixture);

    await change('audit-user', 'u2');
    const byUser = entries()[0];
    expect(byUser.request.params.get('userId')).toBe('u2');
    byUser.flush(pageOf([]));
    await settle(fixture);

    await change('audit-from', '2027-01-01');
    entries()[0].flush(pageOf([]));
    await settle(fixture);
    await change('audit-to', '2027-01-31');
    const byDate = entries()[0];
    expect(byDate.request.params.get('from')).toBe('2027-01-01');
    expect(byDate.request.params.get('to')).toBe('2027-01-31');
    byDate.flush(pageOf([]));
    await settle(fixture);

    expect(root.textContent).toContain(ui.auditLog.empty);
  });

  it('does not ask for an end date before the start date', async () => {
    entries()[0].flush(pageOf([update]));
    await settle(fixture);
    await change('audit-to', '2027-01-01');
    entries()[0].flush(pageOf([update]));
    await settle(fixture);

    await change('audit-from', '2027-02-01');

    expect(entries()).toHaveLength(0);
    expect(root.textContent).toContain(ui.auditLog.rangeInvalid);
    expect(control('audit-from').getAttribute('aria-invalid')).toBe('true');
  });
});
