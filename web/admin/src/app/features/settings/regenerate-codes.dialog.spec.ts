import { DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { errorMessages, ui } from '../../core/i18n/tr';
import { RegenerateCodesDialog } from './regenerate-codes.dialog';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('RegenerateCodesDialog (V1-03)', () => {
  let fixture: ComponentFixture<RegenerateCodesDialog>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const ref = { close: vi.fn(), disableClose: false };

  beforeEach(() => {
    ref.close.mockClear();
    ref.disableClose = false;
    TestBed.configureTestingModule({
      imports: [RegenerateCodesDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DialogRef, useValue: ref },
      ],
    });
    fixture = TestBed.createComponent(RegenerateCodesDialog);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  const submit = () =>
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));

  function type(value: string): void {
    const input = root.querySelector<HTMLInputElement>('#regenerate-codes-code')!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('asks for a code first and sends nothing without one', () => {
    submit();
    fixture.detectChanges();

    backend.expectNone('/api/v1/me/mfa/recovery-codes');
    expect(root.textContent).toContain(errorMessages['Field.Required']);
    expect(root.querySelector('#regenerate-codes-code')?.getAttribute('aria-invalid')).toBe('true');
  });

  it('shows the new codes once, keeps the dialog open until they are saved, then closes with true', async () => {
    type('123456');
    submit();
    fixture.detectChanges();
    const request = backend.expectOne('/api/v1/me/mfa/recovery-codes');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ code: '123456' });
    request.flush({ codes: ['aaaa-bbbb-cccc-dddd', 'eeee-ffff-gggg-hhhh'] });
    await settle(fixture);

    expect(root.querySelectorAll(`ul[aria-label="${ui.mfa.setup.codesList}"] li`).length).toBe(2);
    expect(ref.disableClose).toBe(true); // Escape must not drop codes the user has not saved
    const next = [...root.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.mfa.setup.continue,
    )!;
    expect(next.disabled).toBe(true);
    root.querySelector<HTMLInputElement>('#mfa-codes-saved')!.click();
    fixture.detectChanges();
    next.click();

    expect(ref.close).toHaveBeenCalledWith(true);
  });

  it('reports a wrong code and lets the user try again', async () => {
    type('000000');
    submit();
    fixture.detectChanges();
    backend
      .expectOne('/api/v1/me/mfa/recovery-codes')
      .flush({ code: 'Mfa.CodeInvalid' }, { status: 422, statusText: 'Unprocessable Entity' });
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      errorMessages['Mfa.CodeInvalid'],
    );
    expect(root.querySelector('#regenerate-codes-code')).not.toBeNull();
    expect(ref.disableClose).toBe(false);
  });

  it('cancels without a request', () => {
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.actions.cancel)!
      .click();

    expect(ref.close).toHaveBeenCalledWith(false);
  });
});
