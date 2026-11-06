import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { EnrollmentDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { EndEnrollmentDialog, EnrollDialog } from './enrollment-dialogs';
import { StudentEnrollmentsComponent } from './student-enrollments.component';

const active: EnrollmentDto = {
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
const ended: EnrollmentDto = {
  ...active,
  id: 'e0',
  groupId: 'g0',
  groupName: 'U10 Kırmızı',
  startDate: '2025-09-01',
  endDate: '2026-06-30',
  status: 'Ended',
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('StudentEnrollmentsComponent', () => {
  let fixture: ComponentFixture<StudentEnrollmentsComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const open = vi.fn();
  const toast = { success: vi.fn(), error: vi.fn() };

  async function render(studentActive = true): Promise<void> {
    open.mockReset();
    toast.success.mockReset();
    toast.error.mockReset();
    TestBed.configureTestingModule({
      imports: [StudentEnrollmentsComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: toast },
      ],
    });
    fixture = TestBed.createComponent(StudentEnrollmentsComponent);
    fixture.componentRef.setInput('studentId', 'st1');
    fixture.componentRef.setInput('studentName', 'Ayşe Yılmaz');
    fixture.componentRef.setInput('studentActive', studentActive);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
  }

  afterEach(() => backend.verify());

  const list = () => backend.expectOne('/api/v1/students/st1/enrollments');
  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text));

  it('lists active and ended enrollments with groups and dates', async () => {
    await render();
    list().flush([active, ended]);
    await settle(fixture);

    expect(root.textContent).toContain(ui.enrollments.active);
    expect(root.textContent).toContain(ui.enrollments.ended);
    expect(root.querySelector('a[href="/groups/g1"]')?.textContent).toContain('U12 Mavi');
    expect(root.textContent).toContain('01.09.2026');
    expect(root.textContent).toContain('30.06.2026');
    expect(root.textContent).toContain(ui.enrollments.statuses['Ended']);
  });

  it('opens the enroll dialog for the student and reloads even when cancelled', async () => {
    await render();
    list().flush([]);
    await settle(fixture);
    expect(root.textContent).toContain(ui.enrollments.none);

    open.mockReturnValue({ closed: of(undefined) });
    buttonWith(ui.enrollments.enroll)!.click();
    await settle(fixture);

    expect(open).toHaveBeenCalledWith(
      EnrollDialog,
      expect.objectContaining({
        data: { student: { id: 'st1', name: 'Ayşe Yılmaz' } },
        ariaLabelledBy: 'enroll-title',
      }),
    );
    list().flush([active]);
    await settle(fixture);
    expect(toast.success).not.toHaveBeenCalled();
    expect(root.textContent).toContain('U12 Mavi');
  });

  it('ends an enrollment and shows a notice when it was already ended', async () => {
    await render();
    list().flush([active]);
    await settle(fixture);
    open.mockReturnValue({ closed: of('alreadyEnded') });

    (
      root.querySelector(
        `button[aria-label="${ui.enrollments.end}: U12 Mavi"]`,
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);
    expect(open).toHaveBeenCalledWith(EndEnrollmentDialog, expect.anything());
    list().flush([{ ...active, status: 'Ended', endDate: '2026-11-01' }]);
    await settle(fixture);

    expect(toast.error).toHaveBeenCalledWith(errorMessages['Enrollment.AlreadyEnded']);
    expect(root.textContent).toContain(ui.enrollments.ended);
  });

  it('does not offer enrollment for a student who left', async () => {
    await render(false);
    list().flush([ended]);
    await settle(fixture);
    expect(buttonWith(ui.enrollments.enroll)).toBeUndefined();
    expect(root.textContent).toContain(ui.enrollments.studentLeft);
  });

  it('offers a retry when loading fails', async () => {
    await render();
    list().flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Sunucuya ulaşılamıyor');
    buttonWith(ui.common.retry)!.click();
    await settle(fixture);
    list().flush([]);
    await settle(fixture);
    expect(root.textContent).toContain(ui.enrollments.none);
  });
});
