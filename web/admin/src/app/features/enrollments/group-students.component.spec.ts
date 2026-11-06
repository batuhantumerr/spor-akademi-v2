import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { EnrollmentDto, GroupDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { EndEnrollmentDialog } from './enrollment-dialogs';
import { GroupStudentsComponent } from './group-students.component';

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

const page = (items: EnrollmentDto[], totalCount = items.length) => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount,
  totalPages: 1,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('GroupStudentsComponent', () => {
  let fixture: ComponentFixture<GroupStudentsComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const open = vi.fn();
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(async () => {
    open.mockReset();
    toast.success.mockReset();
    toast.error.mockReset();
    TestBed.configureTestingModule({
      imports: [GroupStudentsComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: toast },
        { provide: AuthService, useValue: { user: signal(null) } },
      ],
    });
    fixture = TestBed.createComponent(GroupStudentsComponent);
    fixture.componentRef.setInput('group', group);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text));

  /** The list and the capacity count are both GET .../enrollments?status=Active. */
  /** List, active count and the group itself (capacity is re-read from the server). */
  function flushBoth(list: EnrollmentDto[], used: number, capacity = 16): void {
    const requests = backend.match((r) => r.url === '/api/v1/groups/g1/enrollments');
    expect(requests.length).toBe(2);
    for (const request of requests) {
      const isCount = request.request.params.get('pageSize') === '1';
      request.flush(isCount ? page([], used) : page(list));
    }
    backend.expectOne('/api/v1/groups/g1').flush({ ...group, capacity });
  }

  it('shows the capacity as text and as a progress bar', async () => {
    flushBoth([enrollment], 12);
    await settle(fixture);

    expect(root.textContent).toContain('12 / 16');
    const bar = root.querySelector('progress') as HTMLProgressElement;
    expect(bar.value).toBe(12);
    expect(bar.max).toBe(16);
    expect(bar.getAttribute('aria-labelledby')).toBe('capacity-label');
    expect(bar.getAttribute('aria-valuetext')).toBe('12 / 16');
    expect(bar.classList.contains('capacity-full')).toBe(false);
    expect(root.textContent).toContain('Ayşe Yılmaz');
  });

  it('marks a full group', async () => {
    flushBoth([], 16);
    await settle(fixture);
    expect(
      (root.querySelector('progress') as HTMLElement).classList.contains('capacity-full'),
    ).toBe(true);
  });

  it('ends an enrollment and refreshes the list and the capacity', async () => {
    flushBoth([enrollment], 12);
    await settle(fixture);
    open.mockReturnValue({ closed: of({ ...enrollment, status: 'Ended' }) });

    (
      root.querySelector(
        `button[aria-label="${ui.enrollments.end}: Ayşe Yılmaz"]`,
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);

    expect(open).toHaveBeenCalledWith(
      EndEnrollmentDialog,
      expect.objectContaining({ data: { enrollment }, ariaLabelledBy: 'end-enrollment-title' }),
    );
    flushBoth([], 11);
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.enrollments.endedToast);
    expect(root.textContent).toContain('11 / 16');
    expect(root.textContent).toContain(ui.enrollments.noneInGroup);
  });

  it('shows an unknown state, not "0", while loading and after a failure, with a retry', async () => {
    const bar = () => root.querySelector('progress') as HTMLProgressElement;
    // Nothing flushed yet: loading.
    expect(bar().hasAttribute('value')).toBe(false);
    expect(bar().getAttribute('aria-valuetext')).toBe(ui.enrollments.capacityLoading);

    const requests = backend.match((r) => r.url === '/api/v1/groups/g1/enrollments');
    for (const request of requests) {
      if (request.request.params.get('pageSize') === '1') {
        request.flush(null, { status: 0, statusText: '' });
      } else {
        request.flush(page([]));
      }
    }
    backend.expectOne('/api/v1/groups/g1').flush(group);
    await settle(fixture);

    expect(bar().hasAttribute('value')).toBe(false);
    expect(bar().getAttribute('aria-valuetext')).toBe(ui.enrollments.capacityUnknown);
    expect(root.textContent).toContain(ui.enrollments.capacityFailed);
    expect(root.textContent).not.toContain('0 / 16');

    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.common.retry)!
      .click();
    await settle(fixture);
    backend
      .expectOne(
        (r) => r.url === '/api/v1/groups/g1/enrollments' && r.params.get('pageSize') === '1',
      )
      .flush(page([], 7));
    backend.expectOne('/api/v1/groups/g1').flush(group);
    await settle(fixture);
    expect(bar().getAttribute('aria-valuetext')).toBe('7 / 16');
  });

  it('re-reads capacity from the server after any dialog closes, even a failed enroll', async () => {
    flushBoth([], 15);
    await settle(fixture);
    // The enroll dialog is cancelled after the API said the group is full.
    open.mockReturnValue({ closed: of(undefined) });
    buttonWith(ui.enrollments.enrollStudent)!.click();
    await settle(fixture);

    // Meanwhile the capacity was raised to 20 and someone else enrolled.
    flushBoth([], 16, 20);
    await settle(fixture);
    expect(root.textContent).toContain('16 / 20');
    expect(toast.success).not.toHaveBeenCalled();
  });

  it('says so and reloads when the enrollment was already ended', async () => {
    flushBoth([enrollment], 12);
    await settle(fixture);
    open.mockReturnValue({ closed: of('alreadyEnded') });
    buttonWith(ui.enrollments.end)!.click();
    await settle(fixture);
    flushBoth([], 11);
    await settle(fixture);

    expect(toast.error).toHaveBeenCalledWith(errorMessages['Enrollment.AlreadyEnded']);
    expect(root.textContent).toContain('11 / 16');
  });

  it('filters by enrollment status', async () => {
    flushBoth([enrollment], 12);
    await settle(fixture);
    const select = root.querySelector('#enrollment-status') as HTMLSelectElement;
    select.value = 'Ended';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    const request = backend.expectOne((r) => r.url === '/api/v1/groups/g1/enrollments');
    expect(request.request.params.get('status')).toBe('Ended');
    request.flush(page([]));
    await settle(fixture);
  });
});
