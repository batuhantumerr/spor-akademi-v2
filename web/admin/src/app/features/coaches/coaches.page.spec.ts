import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { isSignal } from '@angular/core';
import { of } from 'rxjs';

import { CoachDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { TemporaryPasswordService } from '../../shared/ui/temporary-password.dialog';
import { ToastService } from '../../shared/ui/toast.service';
import { CoachesPage } from './coaches.page';

const mert: CoachDto = {
  id: 'c1',
  firstName: 'Mert',
  lastName: 'Kaya',
  email: 'mert@example.test',
  phone: '+905321234567',
  status: 'Active',
  version: 1,
};
const eski: CoachDto = { ...mert, id: 'c2', firstName: 'Eski', status: 'Deactivated', phone: null };

const pageOf = (items: CoachDto[]) => ({
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

describe('CoachesPage', () => {
  let fixture: ComponentFixture<CoachesPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const open = vi.fn();
  const show = vi.fn().mockResolvedValue(undefined);
  const confirm = vi.fn<() => Promise<boolean>>();
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(() => {
    open.mockReset();
    show.mockClear();
    confirm.mockReset().mockResolvedValue(true);
    toast.success.mockReset();
    toast.error.mockReset();
    TestBed.configureTestingModule({
      imports: [CoachesPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Dialog, useValue: { open } },
        { provide: TemporaryPasswordService, useValue: { show } },
        { provide: ConfirmService, useValue: { confirm } },
        { provide: ToastService, useValue: toast },
      ],
    });
    fixture = TestBed.createComponent(CoachesPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => backend.verify());

  const list = () => backend.expectOne((r) => r.url === '/api/v1/coaches');
  const byLabel = (label: string) =>
    root.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement | null;

  async function loadWith(items: CoachDto[]): Promise<void> {
    list().flush(pageOf(items));
    await settle(fixture);
  }

  it('lists coaches with e-mail, formatted phone and status', async () => {
    await loadWith([mert, eski]);
    expect(root.textContent).toContain('Mert Kaya');
    expect(root.textContent).toContain('mert@example.test');
    expect(root.textContent).toContain('0532 123 45 67');
    expect(root.textContent).toContain('Pasif');
    // Inactive coaches have no deactivate or reset actions.
    expect(byLabel(`${ui.coaches.deactivate}: Eski Kaya`)).toBeNull();
    expect(byLabel(`${ui.coaches.resetPassword}: Eski Kaya`)).toBeNull();
  });

  it('shows the temporary password of a new coach once and keeps nothing', async () => {
    await loadWith([]);
    open.mockReturnValue({
      closed: of({ kind: 'created', name: 'Mert Kaya', temporaryPassword: 'tmp-456' }),
    });

    (root.querySelector('.btn-primary') as HTMLButtonElement).click();
    await settle(fixture);
    list().flush(pageOf([mert]));
    await settle(fixture);

    expect(show).toHaveBeenCalledWith({ name: 'Mert Kaya', password: 'tmp-456' });
    expect(toast.success).toHaveBeenCalledWith(ui.coaches.created);
    expect(pageState(fixture.componentInstance)).not.toContain('tmp-456');
    expect(root.innerHTML).not.toContain('tmp-456');
  });

  it('opens the edit dialog with the coach and reloads afterwards', async () => {
    await loadWith([mert]);
    open.mockReturnValue({ closed: of({ kind: 'saved' }) });

    byLabel(`${ui.actions.edit}: Mert Kaya`)!.click();
    await settle(fixture);
    list().flush(pageOf([mert]));
    await settle(fixture);

    expect(open).toHaveBeenCalledWith(
      expect.anything(),
      expect.objectContaining({ data: mert, ariaLabelledBy: 'coach-form-title' }),
    );
    expect(toast.success).toHaveBeenCalledWith(ui.coaches.saved);
  });

  it('deactivates after confirmation', async () => {
    await loadWith([mert]);
    byLabel(`${ui.coaches.deactivate}: Mert Kaya`)!.click();
    await settle(fixture);

    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({ title: ui.coaches.deactivateTitle, destructive: true }),
    );
    backend.expectOne('/api/v1/coaches/c1/deactivate').flush({ ...mert, status: 'Deactivated' });
    await settle(fixture);
    list().flush(pageOf([{ ...mert, status: 'Deactivated' }]));
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.coaches.deactivated);
  });

  it('shows the API message when the coach is already inactive', async () => {
    await loadWith([mert]);
    byLabel(`${ui.coaches.deactivate}: Mert Kaya`)!.click();
    await settle(fixture);
    backend
      .expectOne('/api/v1/coaches/c1/deactivate')
      .flush({ code: 'User.AlreadyInactive' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    list().flush(pageOf([mert]));
    await settle(fixture);

    expect(toast.error).toHaveBeenCalledWith('Bu kullanıcı zaten pasif.');
  });

  it('resets the password after confirmation and shows the new one once', async () => {
    await loadWith([mert]);
    byLabel(`${ui.coaches.resetPassword}: Mert Kaya`)!.click();
    await settle(fixture);
    backend.expectOne('/api/v1/coaches/c1/reset-password').flush({ temporaryPassword: 'tmp-789' });
    await settle(fixture);

    expect(show).toHaveBeenCalledWith({ name: 'Mert Kaya', password: 'tmp-789' });
    expect(pageState(fixture.componentInstance)).not.toContain('tmp-789');
  });

  it('does not reset when the confirmation is declined', async () => {
    await loadWith([mert]);
    confirm.mockResolvedValueOnce(false);
    byLabel(`${ui.coaches.resetPassword}: Mert Kaya`)!.click();
    await settle(fixture);
    backend.expectNone('/api/v1/coaches/c1/reset-password');
    expect(show).not.toHaveBeenCalled();
  });
});
