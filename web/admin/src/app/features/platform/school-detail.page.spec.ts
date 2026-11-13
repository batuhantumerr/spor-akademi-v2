import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SchoolDto, SchoolFeatureDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { SchoolDetailPage } from './school-detail.page';

const school: SchoolDto = {
  id: 's1',
  name: 'Kartal Spor',
  slug: 'kartal-spor',
  status: 'Active',
  timeZone: 'Europe/Istanbul',
};

const reports: SchoolFeatureDto = {
  key: 'reports',
  description: 'School Admin reports.',
  enabled: true,
  defaultEnabled: true,
  overridden: false,
};

const payments: SchoolFeatureDto = {
  key: 'parent-online-payments',
  description: 'Online payment of dues from the parent app.',
  enabled: false,
  defaultEnabled: true,
  overridden: true,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('SchoolDetailPage (P4-10)', () => {
  let fixture: ComponentFixture<SchoolDetailPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toasts = { success: vi.fn(), error: vi.fn() };

  beforeEach(async () => {
    toasts.success.mockReset();
    toasts.error.mockReset();
    TestBed.configureTestingModule({
      imports: [SchoolDetailPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ToastService, useValue: toasts },
      ],
    });
    fixture = TestBed.createComponent(SchoolDetailPage);
    fixture.componentRef.setInput('id', 's1');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await settle(fixture);
    backend.expectOne('/api/v1/platform/schools/s1').flush(school);
    backend.expectOne('/api/v1/platform/schools/s1/features').flush([reports, payments]);
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const switchOf = (key: string) =>
    root.querySelector(`[data-testid="feature-switch-${key}"]`) as HTMLInputElement;

  it('lists every flag with its state and where the value comes from', () => {
    expect(root.querySelector('h1')?.textContent).toContain('Kartal Spor');
    expect(root.querySelector('caption')?.textContent).toContain(ui.schoolFeatures.caption);
    expect(root.textContent).toContain(ui.schoolFeatures.names['reports']);
    expect(root.textContent).toContain(ui.schoolFeatures.fromSchool);
    expect(switchOf('reports').checked).toBe(true);
    expect(switchOf('parent-online-payments').checked).toBe(false);
  });

  it('names each switch by its row and describes it by its hint (WCAG 4.1.2)', () => {
    const input = switchOf('reports');
    expect(input.getAttribute('role')).toBe('switch');
    expect(input.getAttribute('aria-checked')).toBe('true');
    expect(root.querySelector(`#${input.getAttribute('aria-labelledby')}`)?.textContent).toContain(
      ui.schoolFeatures.names['reports'],
    );
    expect(root.querySelector(`#${input.getAttribute('aria-describedby')}`)?.textContent).toContain(
      ui.schoolFeatures.hints['reports'],
    );
  });

  it('turns a flag off for this school', async () => {
    switchOf('reports').click();
    await settle(fixture);

    const put = backend.expectOne('/api/v1/platform/schools/s1/features/reports');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ enabled: false });
    put.flush({ ...reports, enabled: false, overridden: true });
    await settle(fixture);

    expect(switchOf('reports').checked).toBe(false);
    expect(switchOf('reports').getAttribute('aria-checked')).toBe('false');
    expect(toasts.success).toHaveBeenCalledOnce();
  });

  it('puts the switch back when the change fails', async () => {
    switchOf('reports').click();
    await settle(fixture);

    backend
      .expectOne('/api/v1/platform/schools/s1/features/reports')
      .flush({ code: 'Feature.NotFound' }, { status: 404, statusText: 'Not Found' });
    await settle(fixture);

    expect(switchOf('reports').checked).toBe(true);
    expect(toasts.error).toHaveBeenCalledWith(errorMessages['Feature.NotFound']);
  });
});
