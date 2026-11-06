import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { GroupAttendanceMonthDto, SessionAttendanceDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { parseSessionKey, sessionKey } from '../../core/attendance/attendance';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { GroupAttendanceComponent, addMonths } from './group-attendance.component';
import { StudentAttendanceComponent } from './student-attendance.component';
import { TakeAttendancePage } from './take-attendance.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

const session = (overrides: Partial<SessionAttendanceDto> = {}): SessionAttendanceDto => ({
  groupId: 'g1',
  sessionKey: '2026-11-03T17:00',
  date: '2026-11-03',
  start: '17:00',
  end: '18:30',
  isRecorded: false,
  students: [
    { studentId: 's1', firstName: 'Ayşe', lastName: 'Yılmaz', status: null },
    { studentId: 's2', firstName: 'Can', lastName: 'Er', status: null },
    { studentId: 's3', firstName: 'Ece', lastName: 'Ak', status: null },
  ],
  ...overrides,
});

describe('session keys', () => {
  it('are the planned start as yyyy-MM-ddTHH:mm', () => {
    expect(sessionKey('2026-11-03', '17:00:00')).toBe('2026-11-03T17:00');
    expect(parseSessionKey('2026-11-03T17:00')).toEqual({ date: '2026-11-03', start: '17:00' });
    expect(parseSessionKey('bad')).toBeNull();
    expect(addMonths('2026-12', 1)).toBe('2027-01');
    expect(addMonths('2026-01', -1)).toBe('2025-12');
  });
});

describe('TakeAttendancePage', () => {
  let fixture: ComponentFixture<TakeAttendancePage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(async () => {
    toast.success.mockReset();
    TestBed.configureTestingModule({
      imports: [TakeAttendancePage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toast },
        { provide: AuthService, useValue: { role: signal('Coach'), user: signal(null) } },
      ],
    });
    fixture = TestBed.createComponent(TakeAttendancePage);
    fixture.componentRef.setInput('groupId', 'g1');
    fixture.componentRef.setInput('sessionKey', '2026-11-03T17:00');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  });

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const url = '/api/v1/groups/g1/sessions/2026-11-03T17%3A00/attendance';
  const load = (dto: SessionAttendanceDto) =>
    backend.expectOne((r) => r.url === url && r.method === 'GET').flush(dto);
  const radio = (studentId: string, status: string) =>
    root.querySelector(`input[name="mark-${studentId}"][value="${status}"]`) as HTMLInputElement;
  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text));
  const saveButton = () => buttonWith(ui.attendance.save) as HTMLButtonElement;

  it('shows a row per student with four large choices, grouped and named', async () => {
    load(session());
    await settle(fixture);

    const fieldsets = root.querySelectorAll('fieldset');
    expect(fieldsets.length).toBe(3);
    expect(fieldsets[0].querySelector('legend')?.textContent).toContain('Ayşe Yılmaz');
    const labels = [...fieldsets[0].querySelectorAll('label')].map((l) => l.textContent?.trim());
    expect(labels).toEqual(['Geldi', 'Gelmedi', 'Geç', 'Mazeretli']);
    expect(fieldsets[0].querySelector('label')?.classList.contains('min-h-12')).toBe(true);
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/coach/today');
    expect(saveButton().disabled).toBe(true);
  });

  it('marks everyone present in one tap, allows changes, and saves in one PUT', async () => {
    load(session());
    await settle(fixture);

    buttonWith(ui.attendance.allPresent)!.click();
    await settle(fixture);
    radio('s2', 'Absent').click();
    radio('s3', 'Late').click();
    await settle(fixture);
    expect(root.textContent).toContain('3 / 3 işaretlendi');

    saveButton().click();
    await settle(fixture);
    const put = backend.expectOne((r) => r.url === url && r.method === 'PUT');
    expect(put.request.body).toEqual({
      records: [
        { studentId: 's1', status: 'Present' },
        { studentId: 's2', status: 'Absent' },
        { studentId: 's3', status: 'Late' },
      ],
    });
    put.flush(
      session({
        isRecorded: true,
        students: [
          { studentId: 's1', firstName: 'Ayşe', lastName: 'Yılmaz', status: 'Present' },
          { studentId: 's2', firstName: 'Can', lastName: 'Er', status: 'Absent' },
          { studentId: 's3', firstName: 'Ece', lastName: 'Ak', status: 'Late' },
        ],
      }),
    );
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.attendance.saved);
    expect(root.textContent).toContain(ui.attendance.recorded);
  });

  it('fills only unmarked students when reopened, keeping saved absences', async () => {
    load(
      session({
        isRecorded: true,
        students: [
          { studentId: 's1', firstName: 'Ayşe', lastName: 'Yılmaz', status: 'Absent' },
          { studentId: 's2', firstName: 'Can', lastName: 'Er', status: 'Excused' },
          { studentId: 's3', firstName: 'Ece', lastName: 'Ak', status: null },
        ],
      }),
    );
    await settle(fixture);
    radio('s2', 'Late').click();
    await settle(fixture);

    buttonWith(ui.attendance.allPresent)!.click();
    await settle(fixture);
    expect(radio('s1', 'Absent').checked).toBe(true);
    expect(radio('s2', 'Late').checked).toBe(true);
    expect(radio('s3', 'Present').checked).toBe(true);
  });

  it('marks the chosen option with a check icon and a ring, not colour alone', async () => {
    load(session());
    await settle(fixture);
    radio('s1', 'Late').click();
    await settle(fixture);

    const chosen = radio('s1', 'Late').closest('label')!;
    const other = radio('s1', 'Present').closest('label')!;
    expect(chosen.querySelector('svg')).not.toBeNull();
    expect(chosen.classList.contains('ring-2')).toBe(true);
    expect(chosen.classList.contains('ring-current')).toBe(true);
    expect(chosen.classList.contains('border-2')).toBe(true);
    expect(other.querySelector('svg')).toBeNull();
    expect(other.classList.contains('ring-2')).toBe(false);
  });

  it('shows saved statuses when reopened', async () => {
    load(
      session({
        isRecorded: true,
        students: [
          { studentId: 's1', firstName: 'Ayşe', lastName: 'Yılmaz', status: 'Excused' },
          { studentId: 's2', firstName: 'Can', lastName: 'Er', status: null },
          { studentId: 's3', firstName: 'Ece', lastName: 'Ak', status: 'Present' },
        ],
      }),
    );
    await settle(fixture);
    expect(radio('s1', 'Excused').checked).toBe(true);
    expect(radio('s3', 'Present').checked).toBe(true);
    expect(root.textContent).toContain('2 / 3 işaretlendi');
  });

  it('keeps every selection after a network error and retries with the same marks', async () => {
    load(session());
    await settle(fixture);
    buttonWith(ui.attendance.allPresent)!.click();
    await settle(fixture);
    saveButton().click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === url && r.method === 'PUT')
      .flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    await settle(fixture);

    const alert = root.querySelector('#save-error') as HTMLElement;
    expect(alert.textContent).toContain(ui.attendance.offline);
    expect(document.activeElement).toBe(alert);
    expect(radio('s1', 'Present').checked).toBe(true);
    expect(radio('s3', 'Present').checked).toBe(true);

    alert.querySelector('button')!.click();
    await settle(fixture);
    const retry = backend.expectOne((r) => r.url === url && r.method === 'PUT');
    expect((retry.request.body as { records: unknown[] }).records.length).toBe(3);
    retry.flush(session({ isRecorded: true }));
    await settle(fixture);
    expect(toast.success).toHaveBeenCalled();
  });

  it.each(['Attendance.EditWindowExpired', 'Attendance.SessionInFuture'])(
    'explains %s and keeps the selections',
    async (code) => {
      load(session());
      await settle(fixture);
      radio('s1', 'Late').click();
      await settle(fixture);
      saveButton().click();
      await settle(fixture);
      backend
        .expectOne((r) => r.url === url && r.method === 'PUT')
        .flush({ code }, { status: code.endsWith('Expired') ? 403 : 422, statusText: 'No' });
      await settle(fixture);

      expect(root.querySelector('#save-error')?.textContent).toContain(errorMessages[code]);
      expect(root.querySelector('#save-error button')).toBeNull();
      expect(radio('s1', 'Late').checked).toBe(true);
    },
  );
});

describe('GroupAttendanceComponent', () => {
  let fixture: ComponentFixture<GroupAttendanceComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  const month: GroupAttendanceMonthDto = {
    groupId: 'g1',
    month: '2026-11',
    sessions: [
      {
        sessionKey: '2026-11-03T17:00',
        date: '2026-11-03',
        start: '17:00',
        end: '18:30',
        isRecorded: true,
      },
      {
        sessionKey: '2026-11-05T17:00',
        date: '2026-11-05',
        start: '17:00',
        end: '18:30',
        isRecorded: false,
      },
    ],
    students: [
      {
        studentId: 's1',
        firstName: 'Ayşe',
        lastName: 'Yılmaz',
        cells: [
          { enrolled: true, status: 'Present' },
          { enrolled: true, status: null },
        ],
      },
      {
        studentId: 's2',
        firstName: 'Can',
        lastName: 'Er',
        cells: [
          { enrolled: false, status: null },
          { enrolled: true, status: 'Absent' },
        ],
      },
    ],
  };

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [GroupAttendanceComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { user: signal({ schoolTimeZone: 'Europe/Istanbul' }) } },
      ],
    });
    fixture = TestBed.createComponent(GroupAttendanceComponent);
    fixture.componentRef.setInput('groupId', 'g1');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const request = (m: string) =>
    backend.expectOne(
      (r) => r.url === '/api/v1/groups/g1/attendance' && r.params.get('month') === m,
    );

  it('shows a colour-coded matrix and respects not-enrolled cells', async () => {
    const req = backend.expectOne((r) => r.url === '/api/v1/groups/g1/attendance');
    req.flush(month);
    await settle(fixture);

    const cells = root.querySelectorAll('tbody td [role="img"]');
    expect(cells[0].getAttribute('aria-label')).toBe('Geldi');
    expect(cells[0].className).toContain('bg-success/15');
    expect(cells[1].getAttribute('aria-label')).toBe(ui.attendance.notRecorded);
    expect(cells[2].getAttribute('aria-label')).toBe(ui.attendance.notEnrolled);
    expect(cells[2].classList.contains('cell-not-enrolled')).toBe(true);
    expect(cells[3].getAttribute('aria-label')).toBe('Gelmedi');
  });

  it('links each session column to its edit page', async () => {
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/attendance').flush(month);
    await settle(fixture);
    const link = root.querySelector('thead a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/groups/g1/sessions/2026-11-03T17:00');
    expect(link.getAttribute('aria-label')).toBe('03.11.2026 17:00 yoklamasını düzenle');
  });

  it('does not link sessions that have not happened yet', async () => {
    backend
      .expectOne((r) => r.url === '/api/v1/groups/g1/attendance')
      .flush({
        ...month,
        sessions: [
          ...month.sessions,
          {
            sessionKey: '2999-01-05T17:00',
            date: '2999-01-05',
            start: '17:00',
            end: '18:30',
            isRecorded: false,
          },
        ],
        students: month.students.map((r) => ({
          ...r,
          cells: [...r.cells, { enrolled: true, status: null }],
        })),
      });
    await settle(fixture);
    const headers = root.querySelectorAll('thead th');
    const last = headers[headers.length - 1];
    expect(last.querySelector('a')).toBeNull();
    expect(last.querySelector('span')?.getAttribute('aria-label')).toBe(
      '05.01.2999 17:00 (henüz yapılmadı)',
    );
    expect(root.querySelectorAll('thead a').length).toBe(2);
  });

  it('switches months', async () => {
    const first = backend.expectOne((r) => r.url === '/api/v1/groups/g1/attendance');
    const m = first.request.params.get('month')!;
    first.flush(month);
    await settle(fixture);

    (
      root.querySelector(`button[aria-label="${ui.attendance.previousMonth}"]`) as HTMLButtonElement
    ).click();
    await settle(fixture);
    request(addMonths(m, -1)).flush({ ...month, sessions: [], students: [] });
    await settle(fixture);
    expect(root.textContent).toContain(ui.attendance.noSessions);
  });
});

describe('StudentAttendanceComponent', () => {
  it('shows the rate and the sessions for the range', async () => {
    TestBed.configureTestingModule({
      imports: [StudentAttendanceComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { user: signal(null) } },
      ],
    });
    const fixture = TestBed.createComponent(StudentAttendanceComponent);
    fixture.componentRef.setInput('studentId', 'st1');
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);

    const req = backend.expectOne((r) => r.url === '/api/v1/students/st1/attendance');
    expect(req.request.params.get('from')).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    req.flush({
      studentId: 'st1',
      firstName: 'Ayşe',
      lastName: 'Yılmaz',
      from: '2026-10-09',
      to: '2026-11-07',
      sessions: [
        {
          groupId: 'g1',
          groupName: 'U12 Mavi',
          sessionKey: '2026-11-03T17:00',
          date: '2026-11-03',
          start: '17:00',
          end: '18:30',
          status: 'Late',
        },
      ],
      summary: {
        sessions: 4,
        present: 2,
        late: 1,
        absent: 1,
        excused: 0,
        notRecorded: 0,
        rate: 0.75,
      },
    });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="rate"]')?.textContent?.replace(/\s/g, '')).toBe('%75');
    expect(root.textContent).toContain('4 antrenman · 2 geldi · 1 geç · 1 gelmedi');
    expect(root.textContent).toContain('U12 Mavi');
    expect(root.textContent).toContain('Geç');

    const from = root.querySelector('#att-from') as HTMLInputElement;
    from.value = '2026-01-01';
    from.dispatchEvent(new Event('input'));
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    const next = backend.expectOne((r) => r.url === '/api/v1/students/st1/attendance');
    expect(next.request.params.get('from')).toBe('2026-01-01');
    next.flush({
      studentId: 'st1',
      firstName: 'Ayşe',
      lastName: 'Yılmaz',
      from: '2026-01-01',
      to: '2026-11-07',
      sessions: [],
      summary: {
        sessions: 0,
        present: 0,
        late: 0,
        absent: 0,
        excused: 0,
        notRecorded: 0,
        rate: null,
      },
    });
    await settle(fixture);
    expect(root.textContent).toContain(ui.attendance.rateUnknown);
    expect(root.textContent).toContain(ui.attendance.noHistory);

    // An empty or reversed range cannot be requested.
    const show = [...root.querySelectorAll('button')].find((b) =>
      b.textContent?.includes(ui.attendance.show),
    ) as HTMLButtonElement;
    from.value = '2026-12-01';
    from.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(show.disabled).toBe(true);
    from.value = '';
    from.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(show.disabled).toBe(true);
    backend.verify();
  });
});
