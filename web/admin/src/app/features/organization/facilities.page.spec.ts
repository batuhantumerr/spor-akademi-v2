import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';

import { FacilityDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { FacilitiesPage } from './facilities.page';
import { FacilityFormDialog } from './facility-form.dialog';

const saha: FacilityDto = {
  id: 'f1',
  name: 'Ana saha',
  type: 'Field',
  capacity: 40,
  isArchived: false,
  version: 1,
  address: { line: 'Spor Cd. 1', district: 'Kadıköy', city: 'İstanbul' },
};

const pageOf = (items: FacilityDto[], totalPages = 1, page = 1) => ({
  items,
  page,
  pageSize: 30,
  totalCount: items.length,
  totalPages,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('FacilitiesPage', () => {
  let fixture: ComponentFixture<FacilitiesPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const confirm = vi.fn<() => Promise<boolean>>();
  const open = vi.fn();
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(() => {
    confirm.mockReset().mockResolvedValue(true);
    open.mockReset();
    toast.success.mockReset();
    toast.error.mockReset();
    TestBed.configureTestingModule({
      imports: [FacilitiesPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ConfirmService, useValue: { confirm } },
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: toast },
      ],
    });
    fixture = TestBed.createComponent(FacilitiesPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => backend.verify());

  const list = (page = 1) =>
    backend.expectOne(
      (r) => r.url === '/api/v1/facilities' && r.params.get('page') === String(page),
    );
  const byLabel = (label: string) =>
    root.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement;

  it('shows facilities as cards with type and address', async () => {
    list().flush(pageOf([saha]));
    await settle(fixture);
    expect(root.textContent).toContain('Ana saha');
    expect(root.textContent).toContain('Saha');
    expect(root.textContent).toContain('Kadıköy');
  });

  it('uses a different empty text when archived ones are hidden', async () => {
    list().flush(pageOf([]));
    await settle(fixture);
    expect(root.textContent).toContain(ui.facilities.emptyActive);

    (root.querySelector('input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    const request = list();
    expect(request.request.params.get('includeArchived')).toBe('true');
    request.flush(pageOf([]));
    await settle(fixture);
    expect(root.textContent).toContain(ui.facilities.empty);
  });

  it('keeps the cards on screen while reloading', async () => {
    list().flush(pageOf([saha]));
    await settle(fixture);
    void (fixture.componentInstance as unknown as { list: { load(): Promise<void> } }).list.load();
    await settle(fixture);

    expect(root.textContent).toContain('Ana saha');
    expect(root.querySelector('[aria-busy="true"]')).not.toBeNull();
    list().flush(pageOf([saha]));
    await settle(fixture);
    expect(root.querySelector('[aria-busy="true"]')).toBeNull();
  });

  it('opens one dialog at a time, named by its title, and reloads after it closes', async () => {
    list().flush(pageOf([saha]));
    await settle(fixture);
    const closed = new Subject<boolean>();
    open.mockReturnValue({ closed });

    byLabel(`${ui.actions.edit}: Ana saha`).click();
    await settle(fixture);
    byLabel(`${ui.actions.edit}: Ana saha`).click();

    expect(open).toHaveBeenCalledTimes(1);
    expect(open).toHaveBeenCalledWith(
      FacilityFormDialog,
      expect.objectContaining({ data: saha, ariaLabelledBy: 'facility-form-title' }),
    );
    closed.next(true);
    closed.complete();
    await settle(fixture);
    list().flush(pageOf([saha]));
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.facilities.saved);
  });

  it('clamps to the last page after deleting the only item on it', async () => {
    list().flush(pageOf([saha], 2));
    await settle(fixture);
    byLabel(ui.common.nextPage).click();
    await settle(fixture);
    list(2).flush(pageOf([{ ...saha, id: 'f9', name: 'Havuz' }], 2, 2));
    await settle(fixture);

    byLabel(`${ui.actions.delete}: Havuz`).click();
    await settle(fixture);
    backend.expectOne('/api/v1/facilities/f9').flush(null);
    await settle(fixture);
    list(2).flush(pageOf([], 1, 2));
    await settle(fixture);
    list(1).flush(pageOf([saha], 1));
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith(ui.facilities.deleted);
    expect(root.textContent).toContain('Ana saha');
  });

  it('reports a failed archive as an error toast', async () => {
    list().flush(pageOf([saha]));
    await settle(fixture);
    byLabel(`${ui.actions.archive}: Ana saha`).click();
    await settle(fixture);
    backend
      .expectOne('/api/v1/facilities/f1/archive')
      .flush({ code: 'Facility.NotFound' }, { status: 404, statusText: 'Not Found' });
    await settle(fixture);
    list().flush(pageOf([]));
    await settle(fixture);

    expect(toast.error).toHaveBeenCalledWith('Tesis bulunamadı.');
  });
});
