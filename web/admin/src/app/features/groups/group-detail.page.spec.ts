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

  afterEach(() => backend.verify());

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
});
