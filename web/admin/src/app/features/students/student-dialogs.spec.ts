import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GuardianDto, StudentDto } from '../../api/models';
import { AuthService, CurrentUser } from '../../core/auth/auth.service';
import { ui } from '../../core/i18n/tr';
import {
  GuardianEditDialog,
  StudentEditDialog,
  StudentErasureDialog,
  StudentLeaveDialog,
} from './student-dialogs';

const student: StudentDto = {
  id: 'st1',
  firstName: 'Ayşe',
  lastName: 'Yılmaz',
  birthDate: '2014-05-02',
  gender: 'Female',
  status: 'Active',
  leftAt: null,
  anonymizedAt: null,
  phone: null,
  email: null,
  emergencyContact: { name: 'Ali Kaya', phone: '+905331112233', relationship: 'Komşu' },
  guardians: [],
  version: 4,
};

const guardian: GuardianDto = {
  id: 'gd1',
  firstName: 'Zeynep',
  lastName: 'Yılmaz',
  phone: '+905321234567',
  email: null,
  address: null,
  version: 7,
};

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

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('student dialogs', () => {
  let backend: HttpTestingController;
  let root: HTMLElement;
  let fixture: ComponentFixture<unknown>;
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
        { provide: AuthService, useValue: { user: signal(admin) } },
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

  const input = (id: string) => root.querySelector(`#${id}`) as HTMLInputElement;
  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;
  async function type(id: string, value: string): Promise<void> {
    input(id).value = value;
    input(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function submit(): Promise<void> {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  }

  describe('StudentEditDialog', () => {
    async function conflict(): Promise<void> {
      await open(StudentEditDialog, student);
      await type('e-firstName', 'Ayşegül');
      await submit();
      backend
        .expectOne('/api/v1/students/st1')
        .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
      await settle(fixture);
      backend
        .expectOne('/api/v1/students/st1')
        .flush({ ...student, lastName: 'Yılmaz-Kaya', version: 6 });
      await settle(fixture);
    }

    it('keeps the typed values after a conflict and applies them on the fresh version', async () => {
      await conflict();
      expect(root.textContent).toContain(ui.conflict.title);
      expect(input('e-firstName').value).toBe('Ayşegül');

      buttonWith(ui.conflict.applyMine)!.click();
      await settle(fixture);
      const put = backend.expectOne('/api/v1/students/st1');
      expect(put.request.method).toBe('PUT');
      expect(put.request.body).toEqual(
        expect.objectContaining({ firstName: 'Ayşegül', lastName: 'Yılmaz', version: 6 }),
      );
      put.flush({ ...student, firstName: 'Ayşegül', version: 7 });
      await settle(fixture);
      expect(close).toHaveBeenCalledWith(expect.objectContaining({ firstName: 'Ayşegül' }));
    });

    it('can take the server version instead', async () => {
      await conflict();
      buttonWith(ui.conflict.loadServer)!.click();
      await settle(fixture);

      expect(input('e-firstName').value).toBe('Ayşe');
      expect(input('e-lastName').value).toBe('Yılmaz-Kaya');
      expect(root.textContent).not.toContain(ui.conflict.title);
    });
  });

  describe('GuardianEditDialog', () => {
    async function conflict(): Promise<void> {
      await open(GuardianEditDialog, { phone: guardian.phone });
      backend
        .expectOne((r) => r.url === '/api/v1/guardians')
        .flush({ items: [guardian], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 });
      await settle(fixture);
      await type('ge-email', 'zeynep@example.test');
      await submit();
      backend
        .expectOne('/api/v1/guardians/gd1')
        .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
      await settle(fixture);
    }

    it('keeps the typed values after a conflict and applies them on the fresh version', async () => {
      await conflict();
      backend
        .expectOne((r) => r.url === '/api/v1/guardians')
        .flush({
          items: [{ ...guardian, lastName: 'Yılmaz-Ak', version: 9 }],
          page: 1,
          pageSize: 20,
          totalCount: 1,
          totalPages: 1,
        });
      await settle(fixture);
      expect(input('ge-email').value).toBe('zeynep@example.test');

      buttonWith(ui.conflict.applyMine)!.click();
      await settle(fixture);
      const put = backend.expectOne('/api/v1/guardians/gd1');
      expect(put.request.body).toEqual(
        expect.objectContaining({ email: 'zeynep@example.test', version: 9 }),
      );
      put.flush({ ...guardian, version: 10 });
      await settle(fixture);
      expect(close).toHaveBeenCalled();
    });

    it('explains when the guardian can no longer be found by its phone', async () => {
      await conflict();
      backend
        .expectOne((r) => r.url === '/api/v1/guardians')
        .flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
      await settle(fixture);

      expect(root.textContent).toContain(ui.studentDetail.guardianGone);
      expect(input('ge-email').value).toBe('zeynep@example.test');
      expect(buttonWith(ui.conflict.applyMine)).toBeUndefined();
    });
  });

  describe('StudentErasureDialog', () => {
    const select = (id: string) => root.querySelector(`#${id}`) as HTMLSelectElement;
    async function choose(value: string): Promise<void> {
      select('erasure-reason').value = value;
      select('erasure-reason').dispatchEvent(new Event('change'));
      await settle(fixture);
    }
    async function tick(): Promise<void> {
      const box = input('erasure-confirm');
      box.checked = true;
      box.dispatchEvent(new Event('change'));
      await settle(fixture);
    }

    it('warns that it is irreversible and that an active student leaves today', async () => {
      await open(StudentErasureDialog, student);

      expect(root.textContent).toContain(ui.erasure.irreversible);
      expect(root.textContent).toContain(ui.erasure.activeEffect);
      expect(root.textContent).toContain('Ayşe Yılmaz');
    });

    it('does not mention leaving for a student who already left', async () => {
      await open(StudentErasureDialog, { ...student, status: 'Left', leftAt: '2026-12-01' });

      expect(root.textContent).not.toContain(ui.erasure.activeEffect);
    });

    it('needs a reason and the confirmation before anything is sent', async () => {
      await open(StudentErasureDialog, student);

      await submit();

      expect(root.textContent).toContain(ui.erasure.reasonRequired);
      expect(root.textContent).toContain(ui.erasure.confirmRequired);
      expect(select('erasure-reason').getAttribute('aria-invalid')).toBe('true');
      backend.expectNone('/api/v1/students/st1/erasure');

      await choose('DataSubjectRequest');
      await submit();
      expect(root.textContent).not.toContain(ui.erasure.reasonRequired);
      expect(root.textContent).toContain(ui.erasure.confirmRequired);
      backend.expectNone('/api/v1/students/st1/erasure');
    });

    it('sends the reason and closes with the anonymized student', async () => {
      await open(StudentErasureDialog, student);
      await choose('ConsentWithdrawn');
      await tick();

      await submit();

      const request = backend.expectOne('/api/v1/students/st1/erasure');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ reason: 'ConsentWithdrawn' });
      const erased = {
        ...student,
        firstName: 'Anonim',
        status: 'Left',
        anonymizedAt: '2027-01-14T09:00:00Z',
      };
      request.flush(erased);
      await settle(fixture);
      expect(close).toHaveBeenCalledWith(erased);
    });

    it('shows the API error and stays open', async () => {
      await open(StudentErasureDialog, student);
      await choose('DataSubjectRequest');
      await tick();
      await submit();

      backend
        .expectOne('/api/v1/students/st1/erasure')
        .flush({ code: 'Student.AlreadyAnonymized' }, { status: 422, statusText: 'Unprocessable' });
      await settle(fixture);

      expect(root.querySelector('[role="alert"]')?.textContent).toContain(
        'Bu öğrenci zaten anonimleştirildi.',
      );
      expect(close).not.toHaveBeenCalled();
    });
  });

  describe('school time zone', () => {
    beforeEach(() => {
      vi.stubEnv('TZ', 'UTC');
      vi.useFakeTimers({ toFake: ['Date'] });
      vi.setSystemTime(new Date('2027-01-14T22:30:00Z'));
    });

    afterEach(() => {
      vi.useRealTimers();
      vi.unstubAllEnvs();
    });

    it("defaults and limits the leave date to the school's today", async () => {
      expect(new Date().getDate()).toBe(14);
      await open(StudentLeaveDialog, student);
      expect(input('leave-date').value).toBe('2027-01-15');
      expect(input('leave-date').max).toBe('2027-01-15');
    });

    it("computes the minor/adult hint on the school's today", async () => {
      // 18 on the school's date (15 Jan), still 17 on the browser's (14 Jan).
      await open(StudentEditDialog, { ...student, birthDate: '2009-01-15' });
      expect(root.querySelector('#e-age-hint')?.textContent).toContain('Yetişkin (18 yaş)');
    });
  });
});
