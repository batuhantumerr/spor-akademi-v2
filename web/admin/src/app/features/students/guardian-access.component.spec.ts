import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ParentAccountStatus, StudentGuardianDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { GuardianAccessComponent } from './guardian-access.component';

function guardian(
  status: ParentAccountStatus,
  options: { email?: string | null; expiresAt?: string | null } = {},
): StudentGuardianDto {
  return {
    guardianId: 'gd1',
    relationship: 'Mother',
    firstName: 'Zeynep',
    lastName: 'Yılmaz',
    phone: '+905321234567',
    email: options.email === undefined ? 'zeynep@example.test' : options.email,
    invitation: { guardianId: 'gd1', status, invitationExpiresAt: options.expiresAt ?? null },
  };
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('GuardianAccessComponent (P3-02)', () => {
  let fixture: ComponentFixture<GuardianAccessComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  let changed: number;
  const text = ui.parentAccess;
  const toast = { success: vi.fn(), error: vi.fn() };
  const confirm = vi.fn<() => Promise<boolean>>();

  function render(value: StudentGuardianDto): void {
    fixture = TestBed.createComponent(GuardianAccessComponent);
    fixture.componentRef.setInput('guardian', value);
    changed = 0;
    fixture.componentInstance.changed.subscribe(() => changed++);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  }

  function button(label: string): HTMLButtonElement {
    const found = Array.from(root.querySelectorAll('button')).find((b) =>
      b.textContent?.includes(label),
    );
    if (!found) {
      throw new Error(`No button "${label}"`);
    }
    return found;
  }

  function badge(): string {
    return root.querySelector('[data-testid="invitation-status-gd1"]')?.textContent?.trim() ?? '';
  }

  beforeEach(() => {
    toast.success.mockReset();
    toast.error.mockReset();
    confirm.mockReset().mockResolvedValue(true);
    TestBed.configureTestingModule({
      imports: [GuardianAccessComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toast },
        { provide: ConfirmService, useValue: { confirm } },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it.each([
    ['NotInvited', 'Davet edilmedi'],
    ['Invited', 'Davet gönderildi'],
    ['Active', 'Aktif'],
    ['Revoked', 'İptal'],
  ] as const)('shows the %s status as "%s"', (status, label) => {
    render(guardian(status));
    expect(badge()).toBe(label);
  });

  it('invites a guardian with an e-mail address and asks the page to reload', async () => {
    render(guardian('NotInvited'));

    button(text.invite).click();
    const post = backend.expectOne('/api/v1/guardians/gd1/invitation');
    expect(post.request.method).toBe('POST');
    post.flush({ guardianId: 'gd1', status: 'Invited', invitationExpiresAt: null });
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith('Davet zeynep@example.test adresine gönderildi.');
    expect(changed).toBe(1);
  });

  it('cannot invite a guardian without an e-mail address and says why', () => {
    render(guardian('NotInvited', { email: null }));

    expect(button(text.invite).disabled).toBe(true);
    expect(root.textContent).toContain(text.needsEmail);
  });

  it('resends a pending invitation and shows until when the link works', async () => {
    render(guardian('Invited', { expiresAt: '2999-01-17T12:00:00Z' }));
    expect(root.textContent).toContain('17.01.2999 tarihine kadar geçerli');

    button(text.resend).click();
    backend.expectOne('/api/v1/guardians/gd1/invitation').flush({});
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith(text.resent);
  });

  it('says when a pending link has expired', () => {
    render(guardian('Invited', { expiresAt: '2001-01-01T00:00:00Z' }));

    expect(root.textContent).toContain(text.expired);
  });

  it('revokes a pending invitation after confirmation', async () => {
    render(guardian('Invited'));

    button(text.revoke).click();
    await settle(fixture);
    const request = backend.expectOne('/api/v1/guardians/gd1/invitation');
    expect(request.request.method).toBe('DELETE');
    request.flush({ guardianId: 'gd1', status: 'Revoked', invitationExpiresAt: null });
    await settle(fixture);

    expect(confirm).toHaveBeenCalledWith(
      expect.objectContaining({ title: text.revokeTitle, destructive: true }),
    );
    expect(toast.success).toHaveBeenCalledWith(text.revoked);
    expect(changed).toBe(1);
  });

  it('closes an active account only when confirmed', async () => {
    confirm.mockResolvedValue(false);
    render(guardian('Active'));

    button(text.close).click();
    await settle(fixture);

    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ title: text.closeTitle }));
    backend.expectNone('/api/v1/guardians/gd1/invitation');
    expect(changed).toBe(0);
  });

  it('offers a revoked account a new invitation', () => {
    render(guardian('Revoked'));

    expect(button(text.reinvite).disabled).toBe(false);
  });

  it('shows the API error in Turkish, for example an address another account uses', async () => {
    render(guardian('NotInvited'));

    button(text.invite).click();
    backend
      .expectOne('/api/v1/guardians/gd1/invitation')
      .flush({ code: 'User.EmailTaken' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(toast.error).toHaveBeenCalledWith(errorMessages['User.EmailTaken']);
    expect(changed).toBe(1);
  });
});
