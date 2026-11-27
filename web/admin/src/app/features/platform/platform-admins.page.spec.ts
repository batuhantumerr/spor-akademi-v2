import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { PlatformAdminSummary } from '../../core/auth/mfa.service';
import { ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { PlatformAdminsPage } from './platform-admins.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

const admin = (id: string, first: string, mfaEnabled: boolean): PlatformAdminSummary => ({
  id,
  firstName: first,
  lastName: 'Yılmaz',
  email: `${first.toLowerCase()}@example.test`,
  status: 'Active',
  mfaEnabled,
});

describe('PlatformAdminsPage (V1-03)', () => {
  let fixture: ComponentFixture<PlatformAdminsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [PlatformAdminsPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    vi.spyOn(TestBed.inject(AuthService), 'user').mockReturnValue({
      id: 'me',
      email: 'ayse@example.test',
      firstName: 'Ayşe',
      lastName: 'Yılmaz',
      role: 'PlatformAdmin',
      tenantId: null,
      mustChangePassword: false,
    });
    fixture = TestBed.createComponent(PlatformAdminsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  async function load(admins: PlatformAdminSummary[]): Promise<void> {
    backend.expectOne('/api/v1/platform/admins').flush(admins);
    await settle(fixture);
  }

  const resetButtons = () =>
    [...root.querySelectorAll<HTMLButtonElement>('tbody button')].filter(
      (b) => b.textContent?.trim() === ui.mfa.reset.action,
    );

  it('lists the platform admins with their two-step verification state', async () => {
    await load([admin('me', 'Ayşe', true), admin('b', 'Bora', true), admin('c', 'Ceren', false)]);

    expect(root.querySelectorAll('tbody tr').length).toBe(3);
    expect(root.textContent).toContain(ui.mfa.platformAdmins.mfaOn);
    expect(root.textContent).toContain(ui.mfa.platformAdmins.mfaOff);
    expect(root.querySelector('caption')?.textContent).toContain(ui.mfa.platformAdmins.caption);
  });

  it('offers a reset for another admin who has it on, never for yourself', async () => {
    await load([admin('me', 'Ayşe', true), admin('b', 'Bora', true), admin('c', 'Ceren', false)]);

    const buttons = resetButtons();
    expect(buttons.length).toBe(1);
    expect(buttons[0].getAttribute('aria-label')).toContain('Bora Yılmaz');
    expect(root.textContent).toContain(ui.mfa.platformAdmins.selfNote);
  });

  it('resets through the dialog and reloads the list', async () => {
    const dialog = TestBed.inject(Dialog);
    const open = vi.spyOn(dialog, 'open').mockReturnValue({ closed: of(true) } as never);
    const success = vi.spyOn(TestBed.inject(ToastService), 'success');
    await load([admin('me', 'Ayşe', true), admin('b', 'Bora', true)]);

    resetButtons()[0].click();
    await settle(fixture);

    expect(open).toHaveBeenCalledTimes(1);
    expect((open.mock.calls[0][1] as { data: unknown }).data).toEqual({
      userId: 'b',
      name: 'Bora Yılmaz',
    });
    expect(success).toHaveBeenCalledWith(ui.mfa.reset.done);
    backend
      .expectOne('/api/v1/platform/admins')
      .flush([admin('me', 'Ayşe', true), admin('b', 'Bora', false)]);
    await settle(fixture);
    expect(resetButtons().length).toBe(0);
  });

  it('says so when the list cannot be loaded and retries', async () => {
    backend
      .expectOne('/api/v1/platform/admins')
      .flush({ code: 'Error.Unexpected' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')).not.toBeNull();

    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.common.retry)!
      .click();
    await load([admin('b', 'Bora', true)]);

    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelectorAll('tbody tr').length).toBe(1);
  });
});
