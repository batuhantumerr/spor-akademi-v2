import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { EnrollmentDto, GroupDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import {
  EndEnrollmentDialog,
  EnrollDialog,
  STUDENT_SEARCH_DEBOUNCE_MS,
} from './enrollment-dialogs';
import { enrollProblem } from './enrollment-errors';

const group: GroupDto = {
  id: 'g1',
  name: 'U12 Mavi',
  sport: { id: 'sp1', name: 'Futbol' },
  facility: { id: 'f1', name: 'Ana saha' },
  coach: null,
  birthYearRange: { from: 2014, to: 2015 },
  capacity: 16,
  status: 'Active',
  archivedOn: null,
  slots: [],
  version: 3,
};

const enrollment: EnrollmentDto = {
  id: 'e1',
  studentId: 'st1',
  studentFirstName: 'Ayşe',
  studentLastName: 'Yılmaz',
  groupId: 'g1',
  groupName: 'U12 Mavi',
  startDate: '2026-09-01',
  endDate: null,
  status: 'Active',
};

const page = <T>(items: T[], totalCount = items.length) => ({
  items,
  page: 1,
  pageSize: 10,
  totalCount,
  totalPages: 1,
});

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await wait(0);
  fixture.detectChanges();
}

describe('enrollProblem', () => {
  it('puts pair problems on whatever the dialog lets the user pick', () => {
    const problem = {
      code: 'Enrollment.BirthYearOutOfRange',
      args: { year: 2012, from: 2014, to: 2015 },
    };
    expect(enrollProblem(problem, 'group')).toEqual({
      target: 'group',
      message: 'Öğrencinin doğum yılı (2012) bu grubun aralığında (2014–2015) değil.',
    });
    expect(enrollProblem(problem, 'student').target).toBe('student');
  });

  it('keeps group problems on the group picker and student problems on the student picker', () => {
    expect(enrollProblem({ code: 'Enrollment.GroupFull' }, 'group').target).toBe('group');
    expect(enrollProblem({ code: 'Enrollment.GroupFull' }, 'student').target).toBeNull();
    expect(enrollProblem({ code: 'Enrollment.StudentNotActive' }, 'student').target).toBe(
      'student',
    );
  });

  it('shows a lock timeout as a form-level retry hint', () => {
    expect(enrollProblem({ code: 'Lock.Timeout' }, 'group')).toEqual({
      target: null,
      message: errorMessages['Lock.Timeout'],
    });
  });

  it('maps a start-date field error to the date', () => {
    expect(
      enrollProblem(
        { code: 'Validation.Failed', errors: { startDate: ['Field.Invalid'] } },
        'group',
      ).target,
    ).toBe('startDate');
  });
});

describe('enrollment dialogs', () => {
  let fixture: ComponentFixture<unknown>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const close = vi.fn();

  async function open<T>(component: Type<T>, data: unknown): Promise<void> {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [component],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close } },
        { provide: AuthService, useValue: { user: signal(null) } },
      ],
    });
    fixture = TestBed.createComponent(component);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  }

  afterEach(() => {
    root?.remove();
    backend.verify();
  });

  const el = <T extends HTMLElement = HTMLInputElement>(id: string) =>
    root.querySelector(`#${id}`) as T;
  async function submit(): Promise<void> {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  }
  const countRequest = (groupId: string) =>
    backend.expectOne(
      (r) =>
        r.url === `/api/v1/groups/${groupId}/enrollments` && r.params.get('status') === 'Active',
    );

  describe('from a student', () => {
    beforeEach(async () => {
      await open(EnrollDialog, { student: { id: 'st1', name: 'Ayşe Yılmaz' } });
      backend
        .expectOne((r) => r.url === '/api/v1/groups')
        .flush(page([group, { ...group, id: 'g2', name: 'U14 Sarı' }]));
      await settle(fixture);
    });

    async function chooseGroup(id: string, used: number): Promise<void> {
      const select = el<HTMLSelectElement>('enroll-group');
      select.value = id;
      select.dispatchEvent(new Event('change'));
      await settle(fixture);
      countRequest(id).flush(page([], used));
      await settle(fixture);
    }

    it('shows birth years and used/free capacity of the chosen group', async () => {
      await chooseGroup('g1', 12);
      expect(el('enroll-group-facts').textContent).toContain(
        'Doğum yılı 2014–2015 · Kontenjan 12 / 16 (4 boş)',
      );

      await chooseGroup('g2', 16);
      expect(el('enroll-group-facts').textContent).toContain(ui.enrollments.full);
    });

    it("sends the student, group and the school's today as the start date", async () => {
      expect(el('enroll-start').value).toBe(TestBed.inject(SchoolClock).today());
      await chooseGroup('g1', 3);
      await submit();

      const post = backend.expectOne('/api/v1/enrollments');
      expect(post.request.body).toEqual({
        studentId: 'st1',
        groupId: 'g1',
        startDate: TestBed.inject(SchoolClock).today(),
      });
      post.flush(enrollment);
      await settle(fixture);
      expect(close).toHaveBeenCalledWith(enrollment);
    });

    it('requires a group before sending', async () => {
      await submit();
      backend.expectNone('/api/v1/enrollments');
      expect(el('enroll-group-error').textContent).toContain(errorMessages['Field.Required']);
      expect(document.activeElement).toBe(el('enroll-group'));
    });

    it.each([
      [
        'Enrollment.BirthYearOutOfRange',
        { year: 2012, from: 2014, to: 2015 },
        '(2012) bu grubun aralığında (2014–2015)',
      ],
      ['Enrollment.ScheduleConflict', { groupName: 'U10 Kırmızı' }, 'U10 Kırmızı'],
      ['Enrollment.GroupFull', undefined, 'Grup kontenjanı dolu'],
      ['Enrollment.AlreadyEnrolled', undefined, 'zaten kayıtlı'],
      ['Enrollment.GroupArchived', undefined, 'Arşivlenmiş bir gruba'],
    ])('shows %s on the group field', async (code, args, text) => {
      await chooseGroup('g1', 3);
      await submit();
      backend
        .expectOne('/api/v1/enrollments')
        .flush({ code, args }, { status: 422, statusText: 'Unprocessable' });
      await settle(fixture);
      countRequest('g1').flush(page([], 3));
      await settle(fixture);

      expect(el('enroll-group').getAttribute('aria-invalid')).toBe('true');
      expect(el('enroll-group-error').textContent).toContain(text);
      expect(document.activeElement).toBe(el('enroll-group'));
      expect(close).not.toHaveBeenCalled();
    });

    it('shows a lock timeout above the form and keeps the choice for a retry', async () => {
      await chooseGroup('g1', 3);
      await submit();
      backend
        .expectOne('/api/v1/enrollments')
        .flush({ code: 'Lock.Timeout' }, { status: 409, statusText: 'Conflict' });
      await settle(fixture);
      countRequest('g1').flush(page([], 3));
      await settle(fixture);

      expect(el('enroll-error').textContent).toContain(errorMessages['Lock.Timeout']);
      expect(el<HTMLSelectElement>('enroll-group').value).toBe('g1');

      await submit();
      backend.expectOne('/api/v1/enrollments').flush(enrollment);
      await settle(fixture);
      expect(close).toHaveBeenCalledWith(enrollment);
    });
  });

  describe('from a group', () => {
    beforeEach(async () => {
      await open(EnrollDialog, { group });
      countRequest('g1').flush(page([], 15));
      await settle(fixture);
    });

    it('searches active students after a typing pause and enrolls the chosen one', async () => {
      expect(root.textContent).toContain('Kontenjan 15 / 16 (1 boş)');
      const search = el('enroll-search');
      search.value = 'ay';
      search.dispatchEvent(new Event('input'));
      search.value = 'ayşe';
      search.dispatchEvent(new Event('input'));
      await settle(fixture);
      backend.expectNone((r) => r.url === '/api/v1/students');

      await wait(STUDENT_SEARCH_DEBOUNCE_MS + 50);
      const request = backend.expectOne((r) => r.url === '/api/v1/students');
      expect(request.request.params.get('search')).toBe('ayşe');
      expect(request.request.params.get('status')).toBe('Active');
      request.flush(
        page([
          {
            id: 'st1',
            firstName: 'Ayşe',
            lastName: 'Yılmaz',
            birthDate: '2014-05-02',
            gender: 'Female',
            status: 'Active',
            leftAt: null,
          },
        ]),
      );
      await settle(fixture);

      (root.querySelector('input[type="radio"]') as HTMLInputElement).click();
      await settle(fixture);
      await submit();
      const post = backend.expectOne('/api/v1/enrollments');
      expect(post.request.body).toEqual(
        expect.objectContaining({ studentId: 'st1', groupId: 'g1' }),
      );
      post.flush(enrollment);
      await settle(fixture);
      expect(close).toHaveBeenCalled();
    });

    it('asks to pick a student and shows student problems on the picker', async () => {
      await submit();
      backend.expectNone('/api/v1/enrollments');
      expect(el('enroll-student-error').textContent).toContain(ui.enrollments.chooseStudent);
    });
  });

  describe('EndEnrollmentDialog', () => {
    beforeEach(async () => {
      await open(EndEnrollmentDialog, { enrollment });
    });

    it("ends on the school's today by default", async () => {
      const today = TestBed.inject(SchoolClock).today();
      expect(el('end-date').value).toBe(today);
      expect(el('end-date').min).toBe('2026-09-01');
      await submit();
      const post = backend.expectOne('/api/v1/enrollments/e1/end');
      expect(post.request.body).toEqual({ date: today });
      post.flush({ ...enrollment, status: 'Ended', endDate: today });
      await settle(fixture);
      expect(close).toHaveBeenCalledWith(expect.objectContaining({ status: 'Ended' }));
    });

    it('rejects a date before the start on the client', async () => {
      const date = el('end-date');
      date.value = '2026-08-01';
      date.dispatchEvent(new Event('input'));
      await settle(fixture);
      await submit();
      backend.expectNone('/api/v1/enrollments/e1/end');
      expect(el('end-date-error').textContent).toContain(ui.enrollments.endDateHint);
    });

    it('shows the API reason on the date', async () => {
      await submit();
      backend
        .expectOne('/api/v1/enrollments/e1/end')
        .flush({ code: 'Enrollment.InvalidEndDate' }, { status: 422, statusText: 'Unprocessable' });
      await settle(fixture);
      expect(el('end-date-error').textContent).toContain(
        errorMessages['Enrollment.InvalidEndDate'],
      );
      expect(el('end-date').getAttribute('aria-invalid')).toBe('true');
    });
  });
});
