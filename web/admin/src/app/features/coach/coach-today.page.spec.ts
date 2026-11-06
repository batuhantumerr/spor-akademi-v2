import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SessionDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { addDays, isoDate } from '../../core/schedule/schedule';
import { CoachTodayPage } from './coach-today.page';

const session = (date: string, groupName: string): SessionDto => ({
  groupId: `id-${groupName}`,
  groupName,
  date,
  start: '17:00',
  end: '18:30',
  facility: { id: 'f1', name: 'Ana saha' },
});

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('CoachTodayPage', () => {
  let fixture: ComponentFixture<CoachTodayPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const today = isoDate(new Date());

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CoachTodayPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(CoachTodayPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
  });

  afterEach(() => backend.verify());

  const sessionsFor = (date: string) =>
    backend.expectOne((r) => r.url === '/api/v1/me/sessions' && r.params.get('date') === date);
  const byLabel = (label: string) =>
    root.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement;

  it("shows today's sessions as cards linking to attendance", async () => {
    sessionsFor(today).flush([session(today, 'U12 Mavi')]);
    await settle(fixture);

    expect(root.textContent).toContain('U12 Mavi');
    expect(root.textContent).toContain('17:00–18:30');
    expect(root.textContent).toContain('Ana saha');
    const attendance = [...root.querySelectorAll('a')].find((a) =>
      a.textContent?.includes(ui.coachToday.takeAttendance),
    ) as HTMLAnchorElement;
    expect(attendance.getAttribute('href')).toBe(`/coach/sessions/id-U12%20Mavi/${today}T17:00`);
    expect(attendance.classList.contains('h-12')).toBe(true);
  });

  it('switches days with the previous/next buttons and back to today', async () => {
    sessionsFor(today).flush([]);
    await settle(fixture);
    expect(root.textContent).toContain(ui.coachToday.empty);

    byLabel(ui.coachToday.nextDay).click();
    await settle(fixture);
    const tomorrow = addDays(today, 1);
    sessionsFor(tomorrow).flush([session(tomorrow, 'U10 Kırmızı')]);
    await settle(fixture);
    expect(root.textContent).toContain('U10 Kırmızı');
    const [y, m, d] = tomorrow.split('-');
    expect(root.textContent).toContain(`${d}.${m}.${y}`);

    byLabel(ui.coachToday.previousDay).click();
    await settle(fixture);
    byLabel(ui.coachToday.previousDay).click();
    await settle(fixture);
    // The first of the two quick requests is superseded by the second.
    const yesterday = addDays(today, -1);
    const late = sessionsFor(today);
    sessionsFor(yesterday).flush([session(yesterday, 'Dünkü grup')]);
    await settle(fixture);
    late.flush([session(today, 'Bugünkü grup')]);
    await settle(fixture);
    expect(root.textContent).toContain('Dünkü grup');
    expect(root.textContent).not.toContain('Bugünkü grup');

    const backToToday = [...root.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.coachToday.today,
    ) as HTMLButtonElement;
    backToToday.click();
    await settle(fixture);
    sessionsFor(today).flush([]);
    await settle(fixture);
    expect(
      [...root.querySelectorAll('button')].some(
        (b) => b.textContent?.trim() === ui.coachToday.today,
      ),
    ).toBe(false);
  });

  it('offers a retry when loading fails', async () => {
    sessionsFor(today).flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('Sunucuya ulaşılamıyor');
  });
});
