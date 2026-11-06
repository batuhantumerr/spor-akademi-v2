import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GroupDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { GroupFormDialog } from './group-form.dialog';
import { GroupLookups } from './group-lookups';

const lookups: GroupLookups = {
  sports: [{ id: 'sp1', name: 'Futbol' }],
  facilities: [{ id: 'f1', name: 'Ana saha' }],
  coaches: [{ id: 'c1', name: 'Mert Kaya' }],
};

const group: GroupDto = {
  id: 'g1',
  name: 'U12 Mavi',
  sport: { id: 'sp1', name: 'Futbol' },
  facility: { id: 'f-old', name: 'Eski saha' },
  coach: { id: 'c9', firstName: 'Eski', lastName: 'Hoca', isActive: false },
  birthYearRange: { from: 2014, to: 2015 },
  capacity: 20,
  status: 'Active',
  archivedOn: null,
  slots: [],
  version: 6,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('GroupFormDialog', () => {
  let fixture: ComponentFixture<GroupFormDialog>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const close = vi.fn();

  async function open(data: GroupDto | null): Promise<void> {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [GroupFormDialog],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: { group: data, lookups } },
        { provide: DialogRef, useValue: { close } },
      ],
    });
    fixture = TestBed.createComponent(GroupFormDialog);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  }

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const el = <T extends HTMLElement = HTMLInputElement>(id: string) =>
    root.querySelector(`#${id}`) as T;
  async function type(id: string, value: string): Promise<void> {
    el(id).value = value;
    el(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function choose(id: string, value: string): Promise<void> {
    const select = el<HTMLSelectElement>(id);
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
  }
  async function submit(): Promise<void> {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  }

  it('creates a group with sport and without a coach or version', async () => {
    await open(null);
    await type('g-name', 'U10 Kırmızı');
    await choose('g-sport', 'sp1');
    await type('g-from', '2016');
    await type('g-to', '2017');
    await type('g-capacity', '18');
    await choose('g-facility', 'f1');
    await submit();

    const post = backend.expectOne('/api/v1/groups');
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual({
      sportId: 'sp1',
      name: 'U10 Kırmızı',
      birthYearRange: { from: 2016, to: 2017 },
      capacity: 18,
      facilityId: 'f1',
      coachId: null,
    });
    post.flush('g2');
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('edits with the version, without the sport, keeping an inactive coach and archived facility', async () => {
    await open(group);
    expect(el('g-sport').readOnly).toBe(true);
    expect(el<HTMLSelectElement>('g-coach').selectedOptions[0].textContent).toContain(
      'Eski Hoca (pasif)',
    );
    expect(el<HTMLSelectElement>('g-facility').selectedOptions[0].textContent).toContain(
      'Eski saha (arşivde)',
    );

    await choose('g-coach', 'c1');
    await type('g-capacity', '22');
    await submit();

    const put = backend.expectOne('/api/v1/groups/g1');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({
      name: 'U12 Mavi',
      birthYearRange: { from: 2014, to: 2015 },
      capacity: 22,
      facilityId: 'f-old',
      coachId: 'c1',
      version: 6,
    });
    put.flush(group);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('checks the birth year order and capacity on the client', async () => {
    await open(null);
    await type('g-from', '2018');
    await type('g-to', '2016');
    await type('g-capacity', '0');
    await submit();

    backend.expectNone('/api/v1/groups');
    expect(root.querySelector('#g-to-error')?.textContent).toContain(
      errorMessages['BirthYearRange.Invalid'],
    );
    expect(root.querySelector('#g-capacity-error')?.textContent).toContain(
      errorMessages['Group.InvalidCapacity'],
    );
    expect(document.activeElement).toBe(el('g-name'));
  });

  it('puts a taken name and a busy coach on their fields', async () => {
    await open(group);
    await submit();
    backend
      .expectOne('/api/v1/groups/g1')
      .flush(
        { code: 'Schedule.CoachUnavailable', args: { groupName: 'U14 Sarı' } },
        { status: 422, statusText: 'Unprocessable' },
      );
    await settle(fixture);

    expect(root.querySelector('#g-coach-error')?.textContent).toContain('U14 Sarı');
    expect(document.activeElement).toBe(el('g-coach'));
  });

  async function conflict(): Promise<void> {
    await open(group);
    await type('g-name', 'U12 Lacivert');
    await type('g-capacity', '24');
    await submit();
    backend
      .expectOne('/api/v1/groups/g1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush({ ...group, name: 'U12 Beyaz', version: 9 });
    await settle(fixture);
  }

  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;

  it('keeps the edits after a version conflict and can apply them on the fresh version', async () => {
    await conflict();

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(ui.conflict.title);
    // Nothing the user typed was dropped.
    expect(el('g-name').value).toBe('U12 Lacivert');
    expect(el('g-capacity').value).toBe('24');

    buttonWith(ui.conflict.applyMine)!.click();
    await settle(fixture);
    const retry = backend.expectOne('/api/v1/groups/g1');
    expect(retry.request.body).toEqual(
      expect.objectContaining({ name: 'U12 Lacivert', capacity: 24, version: 9 }),
    );
    retry.flush(group);
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(true);
  });

  it('can take the server version instead after a conflict', async () => {
    await conflict();

    buttonWith(ui.conflict.loadServer)!.click();
    await settle(fixture);

    expect(el('g-name').value).toBe('U12 Beyaz');
    expect(el('g-capacity').value).toBe('20');
    expect(buttonWith(ui.conflict.applyMine)).toBeUndefined();

    await submit();
    const next = backend.expectOne('/api/v1/groups/g1');
    expect(next.request.body).toEqual(expect.objectContaining({ name: 'U12 Beyaz', version: 9 }));
    next.flush(group);
    await settle(fixture);
  });

  it('offers a retry when the fresh version cannot be loaded, still keeping the edits', async () => {
    await open(group);
    await type('g-name', 'U12 Lacivert');
    await submit();
    backend
      .expectOne('/api/v1/groups/g1')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush(null, { status: 0, statusText: '' });
    await settle(fixture);

    expect(el('g-name').value).toBe('U12 Lacivert');
    expect(buttonWith(ui.conflict.applyMine)).toBeUndefined();
    buttonWith(ui.common.retry)!.click();
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush({ ...group, version: 9 });
    await settle(fixture);
    expect(buttonWith(ui.conflict.applyMine)).toBeDefined();
  });
});
