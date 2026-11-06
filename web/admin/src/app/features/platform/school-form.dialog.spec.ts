import { DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { errorMessages, ui } from '../../core/i18n/tr';
import { SchoolFormDialog, slugify } from './school-form.dialog';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('slugify', () => {
  it('turns a Turkish school name into a valid slug', () => {
    expect(slugify('Kartal Spor Kulübü')).toBe('kartal-spor-kulubu');
    expect(slugify('  Işık & Çağ  Akademi! ')).toBe('isik-cag-akademi');
  });
});

describe('SchoolFormDialog', () => {
  let fixture: ComponentFixture<SchoolFormDialog>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const close = vi.fn();

  beforeEach(async () => {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [SchoolFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DialogRef, useValue: { close } },
      ],
    });
    fixture = TestBed.createComponent(SchoolFormDialog);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  });

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
  async function fillValid(): Promise<void> {
    await type('s-name', 'Kartal Spor');
    await type('s-admin-first', 'Ayşe');
    await type('s-admin-last', 'Yılmaz');
    await type('s-admin-email', 'ayse@example.test');
  }

  it('defaults the time zone to Europe/Istanbul', () => {
    expect((root.querySelector('#s-timezone') as HTMLSelectElement).value).toBe('Europe/Istanbul');
  });

  it('suggests a slug and checks its format while typing', async () => {
    await type('s-name', 'Kartal Spor');
    expect(input('s-slug').value).toBe('kartal-spor');
    expect(root.querySelector('#s-slug-status')?.textContent).toContain(ui.schools.slugValid);

    await type('s-slug', 'Kartal_Spor');
    expect(input('s-slug').getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#s-slug-status')?.textContent).toContain(
      errorMessages['Slug.Invalid'],
    );
    // Editing the slug by hand stops the suggestion.
    await type('s-name', 'Başka Ad');
    expect(input('s-slug').value).toBe('Kartal_Spor');
  });

  it('sends the school with its first admin and closes with the one-time password', async () => {
    await fillValid();
    await submit();

    const post = backend.expectOne('/api/v1/platform/schools');
    expect(post.request.body).toEqual({
      name: 'Kartal Spor',
      slug: 'kartal-spor',
      timeZone: 'Europe/Istanbul',
      admin: { firstName: 'Ayşe', lastName: 'Yılmaz', email: 'ayse@example.test' },
    });
    post.flush({
      adminId: 'a1',
      temporaryPassword: 'tmp-123',
      school: {
        id: 's1',
        name: 'Kartal Spor',
        slug: 'kartal-spor',
        status: 'Active',
        timeZone: 'Europe/Istanbul',
      },
    });
    await settle(fixture);

    expect(close).toHaveBeenCalledWith({
      schoolName: 'Kartal Spor',
      adminName: 'Ayşe Yılmaz',
      temporaryPassword: 'tmp-123',
    });
  });

  it('puts a taken slug and a taken e-mail on their fields', async () => {
    await fillValid();
    await submit();
    backend
      .expectOne('/api/v1/platform/schools')
      .flush({ code: 'School.SlugTaken' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(input('s-slug').getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#s-slug-status')?.textContent).toContain(
      errorMessages['School.SlugTaken'],
    );
    expect(document.activeElement).toBe(input('s-slug'));

    await type('s-slug', 'kartal-spor-2');
    await submit();
    backend
      .expectOne('/api/v1/platform/schools')
      .flush(
        { code: 'Validation.Failed', errors: { 'admin.email': ['User.EmailTaken'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);

    expect(root.querySelector('#s-admin-email-error')?.textContent).toContain(
      errorMessages['User.EmailTaken'],
    );
    expect(close).not.toHaveBeenCalled();
  });

  it('focuses the first invalid field instead of calling the API', async () => {
    await submit();
    backend.expectNone('/api/v1/platform/schools');
    expect(document.activeElement).toBe(input('s-name'));
  });
});
