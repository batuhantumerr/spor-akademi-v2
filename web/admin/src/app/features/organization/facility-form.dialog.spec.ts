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

  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;

  async function facilityConflict(): Promise<void> {
    await open(facility);
    await type('f-name', 'Benim adım');
    await submit();
    backend
      .expectOne('/api/v1/facilities/f1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    const get = backend.expectOne('/api/v1/facilities/f1');
    expect(get.request.method).toBe('GET');
    get.flush({ ...facility, name: 'Başkasının adı', capacity: 55, version: 7 });
    await settle(fixture);
  }

  it('moves focus to the conflict notice and announces its state politely', async () => {
    await facilityConflict();
    const notice = root.querySelector('.conflict-notice') as HTMLElement;
    expect(document.activeElement).toBe(notice);
    expect(notice.querySelector('[aria-live="polite"]')?.textContent).toContain(ui.conflict.help);
  });

  it('opens the notice again when applying hits a second conflict', async () => {
    await facilityConflict();
    buttonWith(ui.conflict.applyMine)!.click();
    await settle(fixture);
    backend
      .expectOne('/api/v1/facilities/f1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/facilities/f1').flush({ ...facility, version: 8 });
    await settle(fixture);

    expect(root.textContent).toContain(ui.conflict.title);
    expect(input('f-name').value).toBe('Benim adım');
    buttonWith(ui.conflict.applyMine)!.click();
    await settle(fixture);
    const retry = backend.expectOne('/api/v1/facilities/f1');
    expect(retry.request.body).toEqual(expect.objectContaining({ name: 'Benim adım', version: 8 }));
    retry.flush(facility);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('ignores a server version that arrives after the user already chose', async () => {
    await open(facility);
    await type('f-name', 'Benim adım');
    await submit();
    backend
      .expectOne('/api/v1/facilities/f1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    // The first load fails; a retry is in flight when the late first answer would land.
    backend.expectOne('/api/v1/facilities/f1').flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    buttonWith(ui.common.retry)!.click();
    await settle(fixture);
    const retryLoad = backend.expectOne('/api/v1/facilities/f1');
    retryLoad.flush({ ...facility, name: 'Taze', version: 9 });
    await settle(fixture);
    buttonWith(ui.conflict.loadServer)!.click();
    await settle(fixture);

    expect(input('f-name').value).toBe('Taze');
    expect(root.querySelector('.conflict-notice')).toBeNull();
  });

  it('keeps the typed values after a conflict and applies them on the fresh version', async () => {
    await facilityConflict();
    expect(root.textContent).toContain(ui.conflict.title);
    expect(input('f-name').value).toBe('Benim adım');
    expect(close).not.toHaveBeenCalled();

    buttonWith(ui.conflict.applyMine)!.click();
    await settle(fixture);
    const retry = backend.expectOne('/api/v1/facilities/f1');
    expect(retry.request.body).toEqual(
      expect.objectContaining({ name: 'Benim adım', capacity: 40, version: 7 }),
    );
    retry.flush(facility);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('can take the server version instead after a conflict', async () => {
    await facilityConflict();
    buttonWith(ui.conflict.loadServer)!.click();
    await settle(fixture);
    expect(input('f-name').value).toBe('Başkasının adı');
    expect(input('f-capacity').value).toBe('55');
    expect(root.textContent).not.toContain(ui.conflict.title);
  });

  it('keeps the edits and offers a retry when the fresh version cannot be loaded', async () => {
    await open(facility);
    await type('f-name', 'Benim adım');
    await submit();
    backend
      .expectOne('/api/v1/facilities/f1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/facilities/f1').flush(null, { status: 0, statusText: '' });
    await settle(fixture);

    expect(input('f-name').value).toBe('Benim adım');
    expect(buttonWith(ui.conflict.applyMine)).toBeUndefined();
    buttonWith(ui.common.retry)!.click();
    await settle(fixture);
    backend.expectOne('/api/v1/facilities/f1').flush({ ...facility, version: 7 });
    await settle(fixture);
    expect(buttonWith(ui.conflict.applyMine)).toBeDefined();
  });

  it('ignores a second submit while saving', async () => {
    await open(facility);
    await submit();
    await submit();
    backend.expectOne('/api/v1/facilities/f1').flush(facility);
    await settle(fixture);
  });
});
