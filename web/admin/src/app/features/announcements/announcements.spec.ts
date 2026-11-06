import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AnnouncementDto, GroupDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { absoluteTime, relativeTime } from '../../core/format/relative-time';
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

describe('relativeTime in the school time zone (browser on UTC)', () => {
  beforeEach(() => vi.stubEnv('TZ', 'UTC'));
  afterEach(() => vi.unstubAllEnvs());

  // 22:30 UTC on 14 Jan is 01:30 on 15 Jan in Istanbul.
  const now = new Date('2027-01-14T22:30:00Z');

  it("counts days on the school's calendar", () => {
    // 20:00 UTC = 23:00 on 14 Jan in Istanbul: yesterday for the school, same day for the browser.
    expect(relativeTime('2027-01-14T20:00:00Z', now, 'Europe/Istanbul')).toBe('dün');
    expect(relativeTime('2027-01-14T20:00:00Z', now, null)).toBe('2 saat önce');
    // 21:45 UTC = 00:45 on 15 Jan in Istanbul: still today for the school.
    expect(relativeTime('2027-01-14T21:45:00Z', now, 'Europe/Istanbul')).toBe('45 dakika önce');
    expect(relativeTime('2027-01-12T22:00:00Z', now, 'Europe/Istanbul')).toBe('2 gün önce');
  });

  it('formats older and absolute dates in the school zone', () => {
    expect(absoluteTime('2027-01-01T22:30:00Z', 'Europe/Istanbul')).toBe('02.01.2027 01:30');
    expect(absoluteTime('2027-01-01T22:30:00Z', null)).toBe('01.01.2027 22:30');
    expect(relativeTime('2027-01-01T22:30:00Z', now, 'Europe/Istanbul')).toBe('02.01.2027 01:30');
  });
});

describe('AnnouncementItemComponent', () => {
  it("uses the school's zone for relative and absolute dates", async () => {
    vi.stubEnv('TZ', 'UTC');
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { user: signal({ schoolTimeZone: 'Europe/Istanbul' }) } },
      ],
    });
    const fixture = TestBed.createComponent(AnnouncementItemComponent);
    fixture.componentRef.setInput(
      'announcement',
      announcement('a1', { createdAt: '2027-01-14T20:00:00Z' }),
    );
    fixture.componentRef.setInput('now', new Date('2027-01-14T22:30:00Z'));
    await settle(fixture);
    const time = (fixture.nativeElement as HTMLElement).querySelector('time')!;
    expect(time.textContent).toContain('dün');
    expect(time.getAttribute('title')).toBe('14.01.2027 23:00');
    // Reachable without hovering: the exact time is in the accessible text.
    expect(time.querySelector('.sr-only')?.textContent).toContain('Yayımlanma: 14.01.2027 23:00');
    vi.unstubAllEnvs();
  });

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

  it('shows a non-field publish failure in a focused banner and keeps the text', async () => {
    list().flush(page([]));
    await settle(fixture);
    await type('announcement-title', 'Tatil');
    await type('announcement-body', 'Metin');
    await submit();
    backend.expectOne((r) => r.method === 'POST').flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    await settle(fixture);

    const banner = field('announcement-error');
    expect(banner.textContent).toContain('Sunucuya ulaşılamıyor');
    expect(document.activeElement).toBe(banner);
    expect(field<HTMLInputElement>('announcement-title').value).toBe('Tatil');
  });

  it('keeps the publish button focusable while publishing and ignores a second submit', async () => {
    list().flush(page([]));
    await settle(fixture);
    await type('announcement-title', 'Tatil');
    await type('announcement-body', 'Metin');
    const button = root.querySelector('button[type="submit"]') as HTMLButtonElement;
    button.focus();
    await submit();

    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(document.activeElement).toBe(button);
    await submit();
    const post = backend.expectOne((r) => r.method === 'POST');
    post.flush(announcement('a9'));
    await settle(fixture);
    list().flush(page([announcement('a9')]));
    await settle(fixture);

    expect(root.querySelector('[data-testid="announcements-live"]')?.textContent).toContain(
      ui.announcements.published,
    );
    expect(root.querySelector('[data-testid="announcements-live"]')?.textContent).toContain(
      '1 duyuru',
    );
  });

  it('offers a retry when the list fails to load', async () => {
    list().flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    const alert = root.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Sunucuya ulaşılamıyor');
    alert.querySelector('button')!.click();
    await settle(fixture);
    list().flush(page([announcement('a1')]));
    await settle(fixture);
    expect(root.textContent).toContain('Başlık a1');
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

  it('keeps heading levels in order on the full page (h1 then h2)', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsPage);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/me/announcements')
      .flush(page([announcement('a1')]));
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('h1')).not.toBeNull();
    expect(root.querySelector('h2')?.textContent).toContain('Başlık a1');
    expect(root.querySelector('h3')).toBeNull();
  });

  it('marks the compact list busy while loading and offers a retry on error', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsComponent);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[aria-busy="true"]')).not.toBeNull();

    backend
      .expectOne((r) => r.url === '/api/v1/me/announcements')
      .flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    expect(root.querySelector('[aria-busy="true"]')).toBeNull();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Sunucuya ulaşılamıyor');

    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.common.retry)!
      .click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/me/announcements')
      .flush(page([announcement('a1')]));
    await settle(fixture);
    expect(root.textContent).toContain('Başlık a1');
  });

  it('hides "Tüm duyurular" when the list is empty or failed', async () => {
    const fixture = TestBed.createComponent(CoachAnnouncementsComponent);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    backend
      .expectOne((r) => r.url === '/api/v1/me/announcements')
      .flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    expect(root.querySelector('a[href="/coach/announcements"]')).toBeNull();

    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.common.retry)!
      .click();
    await settle(fixture);
    backend.expectOne((r) => r.url === '/api/v1/me/announcements').flush(page([]));
    await settle(fixture);
    expect(root.querySelector('a[href="/coach/announcements"]')).toBeNull();
  });

  it('has a /coach/announcements route behind the coach guard', async () => {
    const { routes } = await import('../../app.routes');
    const coach = routes[2].children!.find((r) => r.path === 'coach')!;
    const route = coach.children!.find((r) => r.path === 'announcements')!;
    expect(route.title).toBe(ui.announcements.title);
    expect(coach.canActivate?.length).toBe(1);
    const component = await (route.loadComponent as () => Promise<unknown>)();
    expect(component).toBe(CoachAnnouncementsPage);
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
