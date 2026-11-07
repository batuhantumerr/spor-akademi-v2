import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { JobsPage } from './jobs.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('JobsPage', () => {
  let fixture: ComponentFixture<JobsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toasts = { success: vi.fn(), error: vi.fn() };
  let open: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    toasts.success.mockClear();
    toasts.error.mockClear();
    open = vi.spyOn(window, 'open').mockReturnValue(null);
    TestBed.configureTestingModule({
      imports: [JobsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toasts },
      ],
    });
    fixture = TestBed.createComponent(JobsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    backend.verify();
    open.mockRestore();
  });

  const button = () => root.querySelector<HTMLButtonElement>('button')!;

  it('starts a panel session first, then opens the panel in a new tab without an opener', async () => {
    button().click();
    fixture.detectChanges();
    expect(open).not.toHaveBeenCalled();
    expect(button().disabled).toBe(true);

    const request = backend.expectOne('/api/v1/platform/jobs/dashboard-session');
    expect(request.request.method).toBe('POST');
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);

    expect(open).toHaveBeenCalledWith('/jobs', '_blank', 'noopener,noreferrer');
    expect(toasts.success).toHaveBeenCalledWith(ui.jobs.opened);
    const fallback = root.querySelector<HTMLAnchorElement>('a[href="/jobs"]');
    expect(fallback?.rel).toBe('noopener noreferrer');
    expect(button().disabled).toBe(false);
  });

  it('does not open the panel when the session is refused', async () => {
    button().click();
    backend
      .expectOne('/api/v1/platform/jobs/dashboard-session')
      .flush({ code: 'Auth.Forbidden' }, { status: 403, statusText: 'Forbidden' });
    await settle(fixture);

    expect(open).not.toHaveBeenCalled();
    expect(toasts.error).toHaveBeenCalled();
    expect(root.querySelector('a[href="/jobs"]')).toBeNull();
  });
});
