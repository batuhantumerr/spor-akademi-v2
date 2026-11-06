import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { StudentDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { StudentDetailPage } from './student-detail.page';

const student: StudentDto = {
  id: 'st1',
  firstName: 'Ayşe',
  lastName: 'Yılmaz',
  birthDate: '2014-05-02',
  gender: 'Female',
  status: 'Active',
  leftAt: null,
  phone: null,
  email: null,
  emergencyContact: { name: 'Ali Kaya', phone: '+905331112233', relationship: 'Komşu' },
  guardians: [
    {
      guardianId: 'gd1',
      relationship: 'Mother',
      firstName: 'Zeynep',
      lastName: 'Yılmaz',
      phone: '+905321234567',
      email: null,
    },
  ],
  version: 4,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('StudentDetailPage', () => {
  let fixture: ComponentFixture<StudentDetailPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { success: vi.fn(), error: vi.fn() };
  const confirm = vi.fn<() => Promise<boolean>>();

  beforeEach(async () => {
    toast.success.mockReset();
    toast.error.mockReset();
    confirm.mockReset().mockResolvedValue(true);
    TestBed.configureTestingModule({
      imports: [StudentDetailPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toast },
        { provide: ConfirmService, useValue: { confirm } },
      ],
    });
    fixture = TestBed.createComponent(StudentDetailPage);
    fixture.componentRef.setInput('id', 'st1');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    backend.expectOne('/api/v1/students/st1').flush(student);
    await settle(fixture);
  });

  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
    backend.verify();
  });

  const buttonWith = (scope: ParentNode, text: string) =>
    [...scope.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;
  const dialogRoot = () => document.querySelector('[role="dialog"]') as HTMLElement;

  it('shows profile, guardians, emergency contact and the enrollments placeholder', () => {
    expect(root.querySelector('h1')?.textContent).toContain('Ayşe Yılmaz');
    expect(root.textContent).toContain('02.05.2014');
    expect(root.textContent).toContain('Zeynep Yılmaz');
    expect(root.textContent).toContain('Anne');
    expect(root.textContent).toContain('0532 123 45 67');
    expect(root.textContent).toContain('Ali Kaya');
    expect(root.textContent).toContain(ui.studentDetail.enrollmentsSoon);
  });

  it('marks the student as left on a chosen date', async () => {
    buttonWith(root, ui.studentDetail.leave)!.click();
    await settle(fixture);

    const dialog = dialogRoot();
    expect(dialog.getAttribute('aria-labelledby')).toBe('student-leave-title');
    const date = dialog.querySelector('#leave-date') as HTMLInputElement;
    expect(date.value).toBe(TestBed.inject(SchoolClock).today());
    date.value = '2026-09-01';
    date.dispatchEvent(new Event('input'));
    await settle(fixture);
    buttonWith(dialog, ui.studentDetail.leave)!.click();
    await settle(fixture);

    const post = backend.expectOne('/api/v1/students/st1/leave');
    expect(post.request.body).toEqual({ date: '2026-09-01' });
    post.flush({ ...student, status: 'Left', leftAt: '2026-09-01', version: 5 });
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith(ui.studentDetail.left);
    expect(root.textContent).toContain('Ayrıldı');
    expect(root.textContent).toContain('01.09.2026');
    expect(buttonWith(root, ui.studentDetail.reactivate)).toBeDefined();
  });

  it('shows the API reason when the leave date is not allowed', async () => {
    buttonWith(root, ui.studentDetail.leave)!.click();
    await settle(fixture);
    const dialog = dialogRoot();
    buttonWith(dialog, ui.studentDetail.leave)!.click();
    await settle(fixture);

    backend
      .expectOne('/api/v1/students/st1/leave')
      .flush({ code: 'Student.InvalidLeaveDate' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);

    expect(dialog.querySelector('#leave-date-error')?.textContent).toContain(
      'Ayrılma tarihi, aktif kayıtların başlangıç tarihinden önce olamaz.',
    );
    expect(dialog.querySelector('#leave-date')?.getAttribute('aria-invalid')).toBe('true');
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('rejects a future leave date on the client', async () => {
    buttonWith(root, ui.studentDetail.leave)!.click();
    await settle(fixture);
    const dialog = dialogRoot();
    const date = dialog.querySelector('#leave-date') as HTMLInputElement;
    date.value = '2999-01-01';
    date.dispatchEvent(new Event('input'));
    await settle(fixture);
    buttonWith(dialog, ui.studentDetail.leave)!.click();
    await settle(fixture);

    backend.expectNone('/api/v1/students/st1/leave');
    expect(dialog.querySelector('#leave-date-error')?.textContent).toContain(
      ui.studentDetail.leaveDateHint,
    );
  });

  it('reactivates a student who left after confirmation', async () => {
    fixture.componentRef.setInput('id', 'st2');
    await settle(fixture);
    backend
      .expectOne('/api/v1/students/st2')
      .flush({ ...student, id: 'st2', status: 'Left', leftAt: '2026-09-01' });
    await settle(fixture);

    buttonWith(root, ui.studentDetail.reactivate)!.click();
    await settle(fixture);
    expect(confirm).toHaveBeenCalled();
    backend
      .expectOne('/api/v1/students/st2/reactivate')
      .flush({ ...student, id: 'st2', status: 'Active', leftAt: null });
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.studentDetail.reactivated);
    expect(buttonWith(root, ui.studentDetail.leave)).toBeDefined();
  });

  it('edits a guardian through a dialog that loads the guardian with its version', async () => {
    (
      root.querySelector(
        `button[aria-label="${ui.studentDetail.editGuardian}: Zeynep Yılmaz"]`,
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/guardians')
      .flush({
        items: [
          {
            id: 'gd1',
            firstName: 'Zeynep',
            lastName: 'Yılmaz',
            phone: '+905321234567',
            email: null,
            address: null,
            version: 7,
          },
        ],
        page: 1,
        pageSize: 20,
        totalCount: 1,
        totalPages: 1,
      });
    await settle(fixture);

    const dialog = dialogRoot();
    const email = dialog.querySelector('#ge-email') as HTMLInputElement;
    email.value = 'zeynep@example.test';
    email.dispatchEvent(new Event('input'));
    await settle(fixture);
    buttonWith(dialog, ui.actions.save)!.click();
    await settle(fixture);

    const put = backend.expectOne('/api/v1/guardians/gd1');
    expect(put.request.body).toEqual(
      expect.objectContaining({ email: 'zeynep@example.test', version: 7, address: null }),
    );
    put.flush({});
    await settle(fixture);
    backend.expectOne('/api/v1/students/st1').flush(student);
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.studentDetail.guardianSaved);
  });
});
