import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { isSignal } from '@angular/core';
import { of } from 'rxjs';

import { SchoolListItem } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { TemporaryPasswordService } from '../../shared/ui/temporary-password.dialog';
import { ToastService } from '../../shared/ui/toast.service';
import { SchoolsPage } from './schools.page';

const school: SchoolListItem = {
  id: 's1',
  name: 'Kartal Spor',
  slug: 'kartal-spor',
  status: 'Active',
  timeZone: 'Europe/Istanbul',
  admin: {
    id: 'a1',
    firstName: 'Ayşe',
    lastName: 'Yılmaz',
    email: 'ayse@example.test',
    status: 'Active',
  },
};

const pageOf = (items: SchoolListItem[]) => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount: items.length,
  totalPages: 1,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/** Everything the page holds in signals, as text. */
function pageState(page: object): string {
  return JSON.stringify(
    Object.values(page).map((value) =>
      isSignal(value)
        ? value()
        : value && typeof value === 'object' && 'items' in value
          ? (value as { items: () => unknown }).items()
          : null,
    ),
  );
}

describe('SchoolsPage', () => {
  let fixture: ComponentFixture<SchoolsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const open = vi.fn();
  const show = vi.fn().mockResolvedValue(undefined);

  beforeEach(() => {
    open.mockReset();
    show.mockClear();
    TestBed.configureTestingModule({
      imports: [SchoolsPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Dialog, useValue: { open } },
        { provide: TemporaryPasswordService, useValue: { show } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(SchoolsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => backend.verify());

  const list = () => backend.expectOne((r) => r.url === '/api/v1/platform/schools');

  it('lists schools with slug, status and first admin', async () => {
    list().flush(pageOf([school, { ...school, id: 's2', name: 'Boş Okul', admin: null }]));
    await settle(fixture);

    expect(root.querySelector('caption')?.textContent).toContain(ui.schools.caption);
    expect(root.textContent).toContain('kartal-spor');
    expect(root.textContent).toContain('Ayşe Yılmaz');
    expect(root.textContent).toContain('ayse@example.test');
    expect(root.textContent).toContain(ui.schools.noAdmin);
    expect(root.querySelector('th[scope="row"]')?.textContent).toContain('Kartal Spor');
  });

  it('shows the empty state', async () => {
    list().flush(pageOf([]));
    await settle(fixture);
    expect(root.textContent).toContain(ui.schools.empty);
  });

  it('hands the temporary password to the one-time dialog without keeping it', async () => {
    list().flush(pageOf([]));
    await settle(fixture);
    open.mockReturnValue({
      closed: of({
        schoolName: 'Kartal Spor',
        adminName: 'Ayşe Yılmaz',
        temporaryPassword: 'tmp-123',
      }),
    });

    (root.querySelector('.btn-primary') as HTMLButtonElement).click();
    await settle(fixture);
    list().flush(pageOf([school]));
    await settle(fixture);

    expect(open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ ariaLabelledBy: 'school-form-title' }),
    );
    expect(show).toHaveBeenCalledWith({
      name: 'Ayşe Yılmaz (Kartal Spor)',
      password: 'tmp-123',
    });
    expect(pageState(fixture.componentInstance)).not.toContain('tmp-123');
    expect(root.innerHTML).not.toContain('tmp-123');
  });

  it('does nothing when the dialog is cancelled', async () => {
    list().flush(pageOf([school]));
    await settle(fixture);
    open.mockReturnValue({ closed: of(undefined) });

    (root.querySelector('.btn-primary') as HTMLButtonElement).click();
    await settle(fixture);

    expect(show).not.toHaveBeenCalled();
    backend.expectNone('/api/v1/platform/schools');
  });
});
