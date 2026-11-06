import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { GroupDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { GroupDetailPage } from './group-detail.page';

const group: GroupDto = {
  id: 'g1',
  name: 'U12 Mavi',
  sport: { id: 'sp1', name: 'Futbol' },
  facility: { id: 'f1', name: 'Ana saha' },
  coach: null,
  birthYearRange: { from: 2014, to: 2015 },
  capacity: 20,
  status: 'Active',
  archivedOn: null,
  slots: [{ day: 'Tuesday', start: '17:00', end: '18:30' }],
  version: 3,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('GroupDetailPage', () => {
  let fixture: ComponentFixture<GroupDetailPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [GroupDetailPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ],
    });
    fixture = TestBed.createComponent(GroupDetailPage);
    fixture.componentRef.setInput('id', 'g1');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush(group);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups/g1/announcements')
      .flush({ items: [], page: 1, pageSize: 10, totalCount: 0, totalPages: 0 });
    backend
      .expectOne((r) => r.url === '/api/v1/groups/g1/sessions')
      .flush([
        {
          groupId: 'g1',
          groupName: 'U12 Mavi',
          date: '2026-11-10',
          start: '17:00',
          end: '18:30',
          facility: { id: 'f1', name: 'Ana saha' },
        },
      ]);
    await settle(fixture);
  });

  afterEach(() => {
    // The announcements section loads on its own; it has its own tests.
    backend.match((r) => r.url.includes('/announcements'));
    backend.verify();
  });

  const save = async () => {
    (
      [...root.querySelectorAll('button')].find((b) =>
        b.textContent?.includes(ui.groupDetail.saveSchedule),
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);
  };

  it('shows key facts, the coach notice and upcoming sessions with Turkish dates', () => {
    expect(root.querySelector('h1')?.textContent).toContain('U12 Mavi');
    expect(root.textContent).toContain('2014–2015');
    expect(root.textContent).toContain(ui.groups.noCoach);
    expect(root.textContent).toContain('Sal 17:00–18:30');
    expect(root.textContent).toContain('Salı, 10.11.2026');
  });

  it('saves the schedule with the version and shows facility warnings', async () => {
    await save();
    const put = backend.expectOne('/api/v1/groups/g1/schedule');
    expect(put.request.body).toEqual({
      slots: [{ day: 'Tuesday', start: '17:00', end: '18:30' }],
      version: 3,
    });
    put.flush({
      id: 'g1',
      slots: group.slots,
      version: 4,
      warnings: [
        { code: 'Schedule.FacilityOverlap', relatedId: 'g2', args: { groupName: 'U14 Sarı' } },
      ],
    });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/sessions').flush([]);
    await settle(fixture);

    expect(root.querySelector('[role="status"]')?.textContent).toContain('U14 Sarı');

    // The next save carries the new version.
    await save();
    const next = backend.expectOne('/api/v1/groups/g1/schedule');
    expect((next.request.body as { version: number }).version).toBe(4);
    next.flush({ id: 'g1', slots: group.slots, version: 5, warnings: [] });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/sessions').flush([]);
    await settle(fixture);
  });

  it('shows a busy coach as an error naming the other group', async () => {
    await save();
    backend
      .expectOne('/api/v1/groups/g1/schedule')
      .flush(
        { code: 'Schedule.CoachUnavailable', args: { groupName: 'U10 Kırmızı' } },
        { status: 422, statusText: 'Unprocessable' },
      );
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      'Antrenörün bu saatte başka bir grubu var: U10 Kırmızı.',
    );
  });

  const buttonWith = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;
  const field = (id: string) => root.querySelector(`#${id}`) as HTMLInputElement;
  async function type(id: string, value: string): Promise<void> {
    field(id).value = value;
    field(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('keeps edited slots after a version conflict and can apply them on the fresh version', async () => {
    await type('slot-end-0', '19:00');
    await save();
    backend
      .expectOne('/api/v1/groups/g1/schedule')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush({ ...group, version: 8 });
    await settle(fixture);

    expect(root.textContent).toContain(ui.conflict.title);
    expect(field('slot-end-0').value).toBe('19:00');

    buttonWith(ui.conflict.applyMine)!.click();
    await settle(fixture);
    const retry = backend.expectOne('/api/v1/groups/g1/schedule');
    expect(retry.request.body).toEqual({
      slots: [{ day: 'Tuesday', start: '17:00', end: '19:00' }],
      version: 8,
    });
    retry.flush({
      id: 'g1',
      slots: [{ day: 'Tuesday', start: '17:00', end: '19:00' }],
      version: 9,
      warnings: [],
    });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/sessions').flush([]);
    await settle(fixture);
    expect(root.textContent).not.toContain(ui.conflict.title);
    expect(field('slot-end-0').value).toBe('19:00');
  });

  it('can discard the edits and load the server schedule after a conflict', async () => {
    await type('slot-end-0', '19:00');
    await save();
    backend
      .expectOne('/api/v1/groups/g1/schedule')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    backend.expectOne('/api/v1/groups/g1').flush({
      ...group,
      slots: [{ day: 'Friday', start: '10:00', end: '11:00' }],
      version: 8,
    });
    await settle(fixture);

    buttonWith(ui.conflict.loadServer)!.click();
    await settle(fixture);
    expect(field('slot-start-0').value).toBe('10:00');
    expect(root.textContent).not.toContain(ui.conflict.title);
  });

  it('keeps the last warnings when a later save fails, and focuses the error naming the slot', async () => {
    document.body.appendChild(root);
    await save();
    backend.expectOne('/api/v1/groups/g1/schedule').flush({
      id: 'g1',
      slots: group.slots,
      version: 4,
      warnings: [
        { code: 'Schedule.FacilityOverlap', relatedId: 'g2', args: { groupName: 'U14 Sarı' } },
      ],
    });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/sessions').flush([]);
    await settle(fixture);

    await save();
    backend.expectOne('/api/v1/groups/g1/schedule').flush(
      {
        code: 'Validation.Failed',
        errors: { 'slots[0]': ['TimeRange.Invalid'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);
    await settle(fixture);

    expect(root.querySelector('[role="status"]')?.textContent).toContain('U14 Sarı');
    const alert = root.querySelector('#schedule-error') as HTMLElement;
    expect(alert.textContent).toContain('1. saat');
    expect(document.activeElement).toBe(alert);
    expect(field('slot-end-0').getAttribute('aria-invalid')).toBe('true');
    root.remove();
  });

  it('starts clean on a new route id and ignores a late response for the old one', async () => {
    fixture.componentRef.setInput('id', 'g2');
    await settle(fixture);
    expect(root.textContent).not.toContain('U12 Mavi');
    const g2 = backend.expectOne('/api/v1/groups/g2');

    fixture.componentRef.setInput('id', 'g3');
    await settle(fixture);
    const g3 = backend.expectOne('/api/v1/groups/g3');
    g3.flush({ ...group, id: 'g3', name: 'U16 Yeşil' });
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/groups/g3/sessions').flush([]);
    await settle(fixture);

    g2.flush({ ...group, id: 'g2', name: 'Eski yanıt' });
    await settle(fixture);
    expect(root.querySelector('h1')?.textContent).toContain('U16 Yeşil');
    expect(root.textContent).not.toContain('Eski yanıt');
  });
});
