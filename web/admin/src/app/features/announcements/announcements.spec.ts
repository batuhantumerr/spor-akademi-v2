import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AnnouncementDto, GroupDto } from '../../api/models';
import { relativeTime } from '../../core/format/relative-time';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { AnnouncementItemComponent } from './announcement-item.component';
import { CoachAnnouncementsComponent } from './coach-announcements.component';
import { CoachAnnouncementsPage } from './coach-announcements.page';
import { BODY_MAX, GroupAnnouncementsComponent } from './group-announcements.component';

const group: GroupDto = {
  id: 'g1',
  name: 'U12 Mavi',
  sport: { id: 'sp1', name: 'Futbol' },
  facility: { id: 'f1', name: 'Ana saha' },
  coach: null,
  birthYearRange: { from: 2014, to: 2015 },
  capacity: 16,
  status: 'Active',
  archivedOn: null,
  slots: [],
  version: 3,
};

const announcement = (id: string, overrides: Partial<AnnouncementDto> = {}): AnnouncementDto => ({
  id,
  groupId: 'g1',
  groupName: 'U12 Mavi',
  title: `Başlık ${id}`,
  body: 'Yarın antrenman yok.',
  createdBy: 'u1',
  createdAt: new Date().toISOString(),
  ...overrides,
});

const page = (items: AnnouncementDto[]) => ({
  items,
  page: 1,
  pageSize: 10,
  totalCount: items.length,
  totalPages: items.length ? 1 : 0,
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('relativeTime', () => {
  const now = new Date('2027-01-14T12:00:00Z');
  it('reads like Turkish speech for recent times and falls back to the date', () => {
    expect(relativeTime('2027-01-14T11:59:40Z', now)).toBe('şimdi');
    expect(relativeTime('2027-01-14T11:55:00Z', now)).toBe('5 dakika önce');
    expect(relativeTime('2027-01-14T09:00:00Z', now)).toBe('3 saat önce');
    expect(relativeTime('2027-01-13T10:00:00Z', now)).toBe('dün');
    expect(relativeTime('2027-01-01T10:00:00Z', now)).toMatch(/^01\.01\.2027 \d\d:00$/);
  });
});

describe('AnnouncementItemComponent', () => {
  it('keeps line breaks and never renders HTML', async () => {
    const fixture = TestBed.createComponent(AnnouncementItemComponent);
    fixture.componentRef.setInput(
      'announcement',
      announcement('a1', { body: 'Satır 1\nSatır 2\n<b>kalın değil</b>' }),
    );
    await settle(fixture);
    const body = (fixture.nativeElement as HTMLElement).querySelector('p.whitespace-pre-line')!;
    expect(body.textContent).toBe('Satır 1\nSatır 2\n<b>kalın değil</b>');
    expect(body.querySelector('b')).toBeNull();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('time')?.getAttribute('datetime'),
    ).toBeTruthy();
  });
});

describe('GroupAnnouncementsComponent', () => {
  let fixture: ComponentFixture<GroupAnnouncementsComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(async () => {
    toast.success.mockReset();
    TestBed.configureTestingModule({
      imports: [GroupAnnouncementsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toast },
      ],
    });
    fixture = TestBed.createComponent(GroupAnnouncementsComponent);
    fixture.componentRef.setInput('group', group);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  });

  afterEach(() => {
    root.remove();
    backend.verify();
  });

  const list = () =>
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/announcements' && r.method === 'GET');
  const field = <T extends HTMLElement>(id: string) => root.querySelector(`#${id}`) as T;
  async function type(id: string, value: string): Promise<void> {
    const el = field<HTMLInputElement>(id);
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function submit(): Promise<void> {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  }

  it('lists the group announcements', async () => {
    list().flush(page([announcement('a1'), announcement('a2')]));
    await settle(fixture);
    expect(root.textContent).toContain('Başlık a1');
    expect(root.textContent).toContain('Başlık a2');
  });

  it('counts characters and limits title and body to the API maximums', async () => {
    list().flush(page([]));
    await settle(fixture);
    expect(field('announcement-title').getAttribute('maxlength')).toBe('150');
    expect(field('announcement-body').getAttribute('maxlength')).toBe(String(BODY_MAX));

    await type('announcement-body', 'Merhaba\nveliler');
    expect(field('announcement-body-count').textContent).toContain('15 / 4000 karakter');
    expect(field('announcement-body').getAttribute('aria-describedby')).toContain(
      'announcement-body-count',
    );
  });

  it('publishes the trimmed text with its line breaks and reloads the list', async () => {
    list().flush(page([]));
    await settle(fixture);
    await type('announcement-title', '  Tatil  ');
    await type('announcement-body', 'Pazartesi antrenman yok.\nSalı normal.\n');
    await submit();

    const post = backend.expectOne(
      (r) => r.url === '/api/v1/groups/g1/announcements' && r.method === 'POST',
    );
    expect(post.request.body).toEqual({
      title: 'Tatil',
      body: 'Pazartesi antrenman yok.\nSalı normal.',
    });
    post.flush(announcement('a3', { title: 'Tatil' }));
    await settle(fixture);
    list().flush(page([announcement('a3', { title: 'Tatil' })]));
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith(ui.announcements.published);
    expect(field<HTMLInputElement>('announcement-title').value).toBe('');
    expect(root.textContent).toContain('Tatil');
  });

  it('requires a title and a body before sending', async () => {
    list().flush(page([]));
    await settle(fixture);
    await submit();
    await settle(fixture);

    backend.expectNone((r) => r.method === 'POST');
    expect(field('announcement-title-error').textContent).toContain(
      errorMessages['Field.Required'],
    );
    expect(document.activeElement).toBe(field('announcement-title'));
  });

  it('shows API field errors on their fields', async () => {
    list().flush(page([]));
    await settle(fixture);
    await type('announcement-title', 'Başlık');
    await type('announcement-body', 'Metin');
    await submit();
    backend
      .expectOne((r) => r.method === 'POST')
      .flush(
        { code: 'Validation.Failed', errors: { body: ['Field.TooLong'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);
    await settle(fixture);

    expect(field('announcement-body-error').textContent).toContain(errorMessages['Field.TooLong']);
    expect(document.activeElement).toBe(field('announcement-body'));
  });

  it('hides the form for an archived group', async () => {
    fixture.componentRef.setInput('group', { ...group, id: 'g9', status: 'Archived' });
    await settle(fixture);
    list().flush(page([]));
    backend.expectOne((r) => r.url === '/api/v1/groups/g9/announcements').flush(page([]));
    await settle(fixture);
    expect(root.querySelector('form')).toBeNull();
    expect(root.textContent).toContain(ui.announcements.archivedGroup);
  });
});

describe('coach announcements', () => {
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  it('shows the newest few on the home screen with group and relative date', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsComponent);
    await settle(fixture);
    const request = backend.expectOne((r) => r.url === '/api/v1/me/announcements');
    expect(request.request.params.get('pageSize')).toBe('3');
    request.flush(
      page([announcement('a1', { createdAt: new Date(Date.now() - 5 * 60_000).toISOString() })]),
    );
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Başlık a1');
    expect(root.textContent).toContain('Grup: U12 Mavi');
    expect(root.textContent).toContain('5 dakika önce');
    expect(root.querySelector('p.line-clamp-2')).not.toBeNull();
    expect(root.querySelector('a[href="/coach/announcements"]')?.textContent).toContain(
      ui.announcements.all,
    );
  });

  it('lists all announcements on the full page', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsPage);
    await settle(fixture);
    const request = backend.expectOne((r) => r.url === '/api/v1/me/announcements');
    expect(request.request.params.get('pageSize')).toBe('20');
    request.flush(page([announcement('a1', { body: 'Uzun\nmetin' })]));
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('h1')?.textContent).toContain(ui.announcements.title);
    expect(root.querySelector('p.whitespace-pre-line')?.textContent).toBe('Uzun\nmetin');
    expect(root.querySelector('p.line-clamp-2')).toBeNull();
  });

  it('says when there is nothing yet', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsComponent);
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/me/announcements').flush(page([]));
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      ui.announcements.emptyCoach,
    );
  });
});
