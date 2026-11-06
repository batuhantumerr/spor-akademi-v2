import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FacilityDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { FacilityFormDialog } from './facility-form.dialog';

const facility: FacilityDto = {
  id: 'f1',
  name: 'Ana saha',
  type: 'Field',
  capacity: 40,
  isArchived: false,
  version: 4,
  address: { line: 'Spor Cd. 1', district: 'Kadıköy', city: 'İstanbul' },
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('FacilityFormDialog', () => {
  let fixture: ComponentFixture<FacilityFormDialog>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const close = vi.fn();

  async function open(data: FacilityDto | null): Promise<void> {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [FacilityFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close } },
      ],
    });
    fixture = TestBed.createComponent(FacilityFormDialog);
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
  const submit = async () => {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  };
  async function type(id: string, value: string): Promise<void> {
    input(id).value = value;
    input(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('is named by its title', async () => {
    await open(null);
    expect(root.querySelector('h2')?.id).toBe('facility-form-title');
    expect(root.querySelector('h2')?.textContent).toContain(ui.facilities.add);
  });

  it('creates without a version', async () => {
    await open(null);
    await type('f-name', 'Havuz');
    await type('f-line', 'Deniz Sk. 2');
    await type('f-district', 'Konak');
    await type('f-city', 'İzmir');
    await submit();

    const post = backend.expectOne('/api/v1/facilities');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).not.toHaveProperty('version');
    expect(post.request.body).toEqual(
      expect.objectContaining({ name: 'Havuz', capacity: null, type: 'Field' }),
    );
    post.flush({ ...facility, id: 'f2' });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('edits with the row version', async () => {
    await open(facility);
    await type('f-name', 'Ana saha (yenilendi)');
    await submit();

    const put = backend.expectOne('/api/v1/facilities/f1');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual(
      expect.objectContaining({ name: 'Ana saha (yenilendi)', version: 4, capacity: 40 }),
    );
    put.flush(facility);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('focuses the first invalid field and links the error to it', async () => {
    await open(null);
    await submit();

    backend.expectNone('/api/v1/facilities');
    expect(document.activeElement).toBe(input('f-name'));
    expect(input('f-name').getAttribute('aria-describedby')).toBe('f-name-error');
    expect(root.querySelector('#f-name-error')).not.toBeNull();
  });

  it('shows API field errors and clears them when the field changes', async () => {
    await open(facility);
    await submit();
    backend.expectOne('/api/v1/facilities/f1').flush(
      {
        code: 'Validation.Failed',
        errors: { name: ['Facility.NameTaken'], 'address.city': ['Field.Required'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);

    expect(root.querySelector('#f-name-error')?.textContent).toContain(
      'Bu adda bir tesis zaten var.',
    );
    expect(input('f-city').getAttribute('aria-invalid')).toBe('true');
    expect(close).not.toHaveBeenCalled();

    await type('f-name', 'Yan saha');
    expect(root.querySelector('#f-name-error')).toBeNull();
    expect(input('f-name').getAttribute('aria-invalid')).toBe('false');
    expect(input('f-city').getAttribute('aria-invalid')).toBe('true');
  });

  it('reloads the facility after a version conflict and retries with the fresh version', async () => {
    await open(facility);
    await type('f-name', 'Benim adım');
    await submit();
    backend
      .expectOne('/api/v1/facilities/f1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    const get = backend.expectOne('/api/v1/facilities/f1');
    expect(get.request.method).toBe('GET');
    get.flush({ ...facility, name: 'Başkasının adı', version: 7 });
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(ui.common.conflictReloaded);
    expect(input('f-name').value).toBe('Başkasının adı');
    expect(close).not.toHaveBeenCalled();

    await submit();
    const retry = backend.expectOne('/api/v1/facilities/f1');
    expect(retry.request.body).toEqual(expect.objectContaining({ version: 7 }));
    retry.flush(facility);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('ignores a second submit while saving', async () => {
    await open(facility);
    await submit();
    await submit();
    backend.expectOne('/api/v1/facilities/f1').flush(facility);
    await settle(fixture);
  });
});
