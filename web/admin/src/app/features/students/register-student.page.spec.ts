import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { GuardianDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { RegisterStudentPage } from './register-student.page';

const mother: GuardianDto = {
  id: 'gd1',
  firstName: 'Zeynep',
  lastName: 'Yılmaz',
  phone: '+905321234567',
  email: null,
  address: null,
  version: 2,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/** A birth date that makes the student 10 years old today. */
function childBirthDate(): string {
  const d = new Date();
  d.setFullYear(d.getFullYear() - 10);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function adultBirthDate(): string {
  return '1990-01-15';
}

describe('RegisterStudentPage', () => {
  let fixture: ComponentFixture<RegisterStudentPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [RegisterStudentPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(RegisterStudentPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  });

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const el = <T extends HTMLElement = HTMLInputElement>(selector: string) =>
    root.querySelector(selector) as T;
  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;
  async function type(selector: string, value: string): Promise<void> {
    const input = el(selector);
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function choose(selector: string, value: string): Promise<void> {
    const select = el<HTMLSelectElement>(selector);
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
  }
  async function fillStudent(birthDate: string): Promise<void> {
    await type('#s-firstName', 'Ayşe');
    await type('#s-lastName', 'Yılmaz');
    await type('#s-birthDate', birthDate);
    await choose('#s-gender', 'Female');
  }
  async function fillEmergency(): Promise<void> {
    await type('#s-emergencyName', 'Ali Kaya');
    await type('#s-emergencyPhone', '0533 111 22 33');
    await type('#s-emergencyRelationship', 'Komşu');
  }
  async function addGuardian(): Promise<string> {
    buttonWith(ui.studentForm.addGuardian)!.click();
    await settle(fixture);
    const lookup = root.querySelectorAll<HTMLInputElement>('[id$="-lookup"]');
    return `#${lookup[lookup.length - 1].id.replace(/-lookup$/, '')}`;
  }
  async function submit(): Promise<void> {
    buttonWith(ui.studentForm.submit)!.click();
    await settle(fixture);
  }

  it('shows the minor/adult hint from the birth date', async () => {
    await type('#s-birthDate', childBirthDate());
    expect(el('#s-age-hint').textContent).toContain('18 yaşından küçük (10 yaş)');
    await type('#s-birthDate', adultBirthDate());
    expect(el('#s-age-hint').textContent).toContain('Yetişkin');
    expect(el('label[for="s-phone"]').textContent).not.toContain('yetişkinler için');
  });

  it('links an existing guardian found by phone', async () => {
    await fillStudent(childBirthDate());
    const g = await addGuardian();
    expect(document.activeElement).toBe(el(`${g}-lookup`));

    await type(`${g}-lookup`, '0532 123 45 67');
    buttonWith(ui.studentForm.lookup)!.click();
    await settle(fixture);
    const lookup = backend.expectOne((r) => r.url === '/api/v1/guardians');
    expect(lookup.request.params.get('phone')).toBe('0532 123 45 67');
    lookup.flush({ items: [mother], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 });
    await settle(fixture);

    expect(root.textContent).toContain('Kayıtlı veli bulundu: Zeynep Yılmaz');
    buttonWith(ui.studentForm.link)!.click();
    await settle(fixture);
    expect(root.textContent).toContain('Bağlandı: Zeynep Yılmaz');

    await fillEmergency();
    await submit();
    const post = backend.expectOne('/api/v1/students');
    expect(post.request.body.guardians).toEqual([
      { guardianId: 'gd1', newGuardian: null, relationship: 'Mother' },
    ]);
    expect(post.request.body.emergencyContact).toEqual({
      name: 'Ali Kaya',
      phone: '0533 111 22 33',
      relationship: 'Komşu',
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    post.flush({ id: 'st1' });
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith(['/students', 'st1']);
  });

  it('enters a new guardian when the phone is unknown, and copies them to the emergency contact', async () => {
    await fillStudent(childBirthDate());
    const g = await addGuardian();
    await type(`${g}-lookup`, '0532 999 88 77');
    buttonWith(ui.studentForm.lookup)!.click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/guardians')
      .flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
    await settle(fixture);

    expect(root.textContent).toContain(ui.studentForm.notFound);
    expect(el(`${g}-phone`).value).toBe('0532 999 88 77');
    await type(`${g}-firstName`, 'Mehmet');
    await type(`${g}-lastName`, 'Yılmaz');
    await choose(`${g}-relationship`, 'Father');

    buttonWith('Veliden kopyala: Mehmet Yılmaz')!.click();
    await settle(fixture);
    expect(el('#s-emergencyName').value).toBe('Mehmet Yılmaz');
    expect(el('#s-emergencyPhone').value).toBe('0532 999 88 77');
    expect(el('#s-emergencyRelationship').value).toBe('Baba');

    await submit();
    const post = backend.expectOne('/api/v1/students');
    expect(post.request.body.guardians).toEqual([
      {
        guardianId: null,
        relationship: 'Father',
        newGuardian: {
          firstName: 'Mehmet',
          lastName: 'Yılmaz',
          phone: '0532 999 88 77',
          email: null,
          address: null,
        },
      },
    ]);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    post.flush({ id: 'st2' });
    await settle(fixture);
    expect(navigate).toHaveBeenCalledWith(['/students', 'st2']);
  });

  it('requires a guardian for a minor and a phone for an adult before sending', async () => {
    await fillStudent(childBirthDate());
    await fillEmergency();
    await submit();
    backend.expectNone('/api/v1/students');
    expect(el('#guardians-error').textContent).toContain(errorMessages['Student.GuardianRequired']);
    // The error summary lists the problem and takes focus; its link jumps to the section.
    const summary = el<HTMLElement>('#error-summary');
    expect(document.activeElement).toBe(summary);
    expect(summary.textContent).toContain('1 sorunu düzeltin');
    expect(summary.textContent).toContain(errorMessages['Student.GuardianRequired']);
    (summary.querySelector('a[href="#guardians-section"]') as HTMLAnchorElement).click();
    expect(document.activeElement).toBe(el('#guardians-section'));

    await type('#s-birthDate', adultBirthDate());
    await submit();
    backend.expectNone('/api/v1/students');
    expect(el('#s-phone-error').textContent).toContain(
      errorMessages['Student.PhoneRequiredForAdult'],
    );
  });

  it('maps API errors to fields and sections', async () => {
    await fillStudent(adultBirthDate());
    await type('#s-phone', '0532 123 45 67');
    const g = await addGuardian();
    buttonWith(ui.studentForm.enterNew)!.click();
    await settle(fixture);
    await type(`${g}-firstName`, 'Mehmet');
    await type(`${g}-lastName`, 'Yılmaz');
    await type(`${g}-phone`, '0532 999 88 77');
    await fillEmergency();
    await submit();

    backend.expectOne('/api/v1/students').flush(
      {
        code: 'Validation.Failed',
        errors: {
          'guardians[0].newGuardian.email': ['Email.Invalid'],
          'emergencyContact.phone': ['PhoneNumber.Invalid'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);

    expect(el(`${g}-email-error`).textContent).toContain(errorMessages['Email.Invalid']);
    expect(el('#s-emergencyPhone-error').textContent).toContain(
      errorMessages['PhoneNumber.Invalid'],
    );
    expect(el('#s-emergencyPhone').getAttribute('aria-invalid')).toBe('true');
    const summary = el<HTMLElement>('#error-summary');
    expect(document.activeElement).toBe(summary);
    expect(summary.querySelectorAll('li').length).toBe(2);
    expect(summary.querySelector(`a[href="${g}-email"]`)?.textContent).toContain(
      errorMessages['Email.Invalid'],
    );
    expect(summary.querySelector('a[href="#s-emergencyPhone"]')).not.toBeNull();

    // Editing a field clears its API error.
    await type('#s-emergencyPhone', '0533 111 22 34');
    expect(el('#s-emergencyPhone-error')).toBeNull();

    await submit();
    backend
      .expectOne('/api/v1/students')
      .flush(
        { code: 'Student.EmergencyContactRequired' },
        { status: 422, statusText: 'Unprocessable' },
      );
    await settle(fixture);
    expect(el('#s-emergency-error').textContent).toContain(
      errorMessages['Student.EmergencyContactRequired'],
    );
  });

  it('ignores a lookup answer that arrives after the phone was changed', async () => {
    const g = await addGuardian();
    await type(`${g}-lookup`, '0532 123 45 67');
    buttonWith(ui.studentForm.lookup)!.click();
    await settle(fixture);
    const first = backend.expectOne((r) => r.url === '/api/v1/guardians');

    await type(`${g}-lookup`, '0532 999 88 77');
    first.flush({ items: [mother], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 });
    await settle(fixture);

    expect(root.textContent).not.toContain('Kayıtlı veli bulundu');
    expect(buttonWith(ui.studentForm.link)).toBeUndefined();
  });

  it('moves focus sensibly after a lookup, a link and an unlink', async () => {
    const g = await addGuardian();
    await type(`${g}-lookup`, '0532 123 45 67');
    buttonWith(ui.studentForm.lookup)!.click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/guardians')
      .flush({ items: [mother], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 });
    await settle(fixture);
    await settle(fixture);
    expect(document.activeElement).toBe(el(`${g}-link`));

    buttonWith(ui.studentForm.link)!.click();
    await settle(fixture);
    expect(document.activeElement).toBe(el(`${g}-relationship`));

    buttonWith(ui.studentForm.unlink)!.click();
    await settle(fixture);
    expect(document.activeElement).toBe(el(`${g}-lookup`));

    buttonWith(ui.studentForm.enterNew)!.click();
    await settle(fixture);
    expect(document.activeElement).toBe(el(`${g}-firstName`));
  });

  it('focuses the first new-guardian field when the lookup finds nobody', async () => {
    const g = await addGuardian();
    await type(`${g}-lookup`, '0532 999 88 77');
    buttonWith(ui.studentForm.lookup)!.click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/guardians')
      .flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
    await settle(fixture);
    await settle(fixture);
    expect(document.activeElement).toBe(el(`${g}-firstName`));
  });

  it('puts Guardian.PhoneTaken on the phone of the only new guardian', async () => {
    await fillStudent(childBirthDate());
    const g = await addGuardian();
    buttonWith(ui.studentForm.enterNew)!.click();
    await settle(fixture);
    await type(`${g}-firstName`, 'Mehmet');
    await type(`${g}-lastName`, 'Yılmaz');
    await type(`${g}-phone`, '0532 999 88 77');
    await fillEmergency();
    await submit();
    backend
      .expectOne('/api/v1/students')
      .flush({ code: 'Guardian.PhoneTaken' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    await settle(fixture);

    expect(el(`${g}-phone-error`).textContent).toContain(errorMessages['Guardian.PhoneTaken']);
    expect(el('#guardians-error')).toBeNull();
  });

  it('allows at most four guardians', async () => {
    for (let i = 0; i < 4; i++) {
      await addGuardian();
    }
    expect(buttonWith(ui.studentForm.addGuardian)!.disabled).toBe(true);
    expect(root.textContent).toContain(ui.studentForm.maxGuardians);
  });
});

describe('RegisterStudentPage age hint on the school date', () => {
  beforeEach(() => {
    vi.stubEnv('TZ', 'UTC');
    vi.useFakeTimers({ toFake: ['Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllEnvs();
  });

  async function render(): Promise<{ root: HTMLElement; fixture: ComponentFixture<unknown> }> {
    TestBed.configureTestingModule({
      imports: [RegisterStudentPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        {
          provide: AuthService,
          useValue: { user: signal({ schoolTimeZone: 'Europe/Istanbul' }) },
        },
      ],
    });
    const fixture = TestBed.createComponent(RegisterStudentPage);
    await settle(fixture);
    return { root: fixture.nativeElement as HTMLElement, fixture };
  }

  async function hintFor(birthDate: string): Promise<string> {
    const { root, fixture } = await render();
    const input = root.querySelector('#s-birthDate') as HTMLInputElement;
    input.value = birthDate;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    return root.querySelector('#s-age-hint')?.textContent ?? '';
  }

  it('treats a 29 February birthday as a year older on 1 March (school date)', async () => {
    // 22:30 UTC on 28 Feb is already 1 March in Istanbul.
    vi.setSystemTime(new Date('2026-02-28T22:30:00Z'));
    expect(await hintFor('2008-02-29')).toContain('Yetişkin (18 yaş)');
  });

  it('keeps a 29 February birthday a minor on 28 February', async () => {
    vi.setSystemTime(new Date('2026-02-28T09:00:00Z'));
    expect(await hintFor('2008-02-29')).toContain('18 yaşından küçük (17 yaş)');
  });
});
