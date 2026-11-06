import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CoachDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { CoachFormDialog } from './coach-form.dialog';

const coach: CoachDto = {
  id: 'c1',
  firstName: 'Mert',
  lastName: 'Kaya',
  email: 'mert@example.test',
  phone: '+905321234567',
  status: 'Active',
  version: 3,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('CoachFormDialog', () => {
  let fixture: ComponentFixture<CoachFormDialog>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const close = vi.fn();

  async function open(data: CoachDto | null): Promise<void> {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [CoachFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close } },
      ],
    });
    fixture = TestBed.createComponent(CoachFormDialog);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  }

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const input = (id: string) => root.querySelector(`#${id}`) as HTMLInputElement;
  async function type(id: string, value: string): Promise<void> {
    input(id).value = value;
    input(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function submit(): Promise<void> {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  }

  it('creates a coach and closes with the one-time password', async () => {
    await open(null);
    await type('c-first', 'Mert');
    await type('c-last', 'Kaya');
    await type('c-email', 'mert@example.test');
    await type('c-phone', '0532 123 45 67');
    await submit();

    const post = backend.expectOne('/api/v1/coaches');
    expect(post.request.body).toEqual({
      firstName: 'Mert',
      lastName: 'Kaya',
      email: 'mert@example.test',
      phone: '0532 123 45 67',
    });
    post.flush({ coach, temporaryPassword: 'tmp-456' });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith({
      kind: 'created',
      name: 'Mert Kaya',
      temporaryPassword: 'tmp-456',
    });
  });

  it('checks the phone format on the client and points to the hint', async () => {
    await open(null);
    await type('c-phone', '12345');
    expect(input('c-phone').getAttribute('aria-invalid')).toBe('true');
    expect(input('c-phone').getAttribute('aria-describedby')).toBe('c-phone-hint c-phone-error');
    expect(root.querySelector('#c-phone-error')?.textContent).toContain(
      errorMessages['PhoneNumber.Invalid'],
    );
    expect(root.querySelector('#c-phone-hint')?.textContent).toContain(ui.coaches.phoneHint);
  });

  it('shows a taken e-mail on the e-mail field', async () => {
    await open(null);
    await type('c-first', 'Mert');
    await type('c-last', 'Kaya');
    await type('c-email', 'mert@example.test');
    await submit();
    backend
      .expectOne('/api/v1/coaches')
      .flush({ code: 'User.EmailTaken' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(root.querySelector('#c-email-error')?.textContent).toContain(
      errorMessages['User.EmailTaken'],
    );
    expect(document.activeElement).toBe(input('c-email'));
  });

  it('edits name and phone with the row version; the e-mail is read-only', async () => {
    await open(coach);
    expect(input('c-email').readOnly).toBe(true);
    expect(input('c-phone').value).toBe('0532 123 45 67');

    await type('c-last', 'Kaya-Demir');
    await submit();
    const put = backend.expectOne('/api/v1/coaches/c1');
    expect(put.request.body).toEqual({
      firstName: 'Mert',
      lastName: 'Kaya-Demir',
      phone: '0532 123 45 67',
      version: 3,
    });
    put.flush(coach);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith({ kind: 'saved' });
  });

  it('reloads the coach after a version conflict and saves with the fresh version', async () => {
    await open(coach);
    await type('c-first', 'Mertcan');
    await submit();
    backend
      .expectOne('/api/v1/coaches/c1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/coaches/c1').flush({ ...coach, firstName: 'Mehmet', version: 8 });
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(ui.common.conflictReloaded);
    expect(input('c-first').value).toBe('Mehmet');

    await submit();
    const retry = backend.expectOne('/api/v1/coaches/c1');
    expect(retry.request.body).toEqual(expect.objectContaining({ version: 8 }));
    retry.flush(coach);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith({ kind: 'saved' });
  });
});
