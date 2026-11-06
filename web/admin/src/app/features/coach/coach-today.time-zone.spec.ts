import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AuthService, CurrentUser } from '../../core/auth/auth.service';
import { CoachTodayPage } from './coach-today.page';

const coach: CurrentUser = {
  id: 'u1',
  email: 'coach@example.test',
  firstName: 'Mert',
  lastName: 'Kaya',
  role: 'Coach',
  tenantId: 't1',
  mustChangePassword: false,
  schoolTimeZone: 'Europe/Istanbul',
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/**
 * The browser runs on UTC while the school is in Istanbul (UTC+3): at 22:30 UTC on 14 January it
 * is already 15 January at the school, and that is the coach's "today".
 */
describe('CoachTodayPage in the school time zone', () => {
  let fixture: ComponentFixture<CoachTodayPage>;
  let backend: HttpTestingController;

  beforeEach(() => {
    vi.stubEnv('TZ', 'UTC');
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2027-01-14T22:30:00Z'));
    TestBed.configureTestingModule({
      imports: [CoachTodayPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { user: signal(coach) } },
      ],
    });
    fixture = TestBed.createComponent(CoachTodayPage);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The announcements section loads on its own; it has its own tests.
    backend.match((r) => r.url.includes('/announcements'));
    backend.verify();
    vi.useRealTimers();
    vi.unstubAllEnvs();
  });

  const sessionsFor = (date: string) =>
    backend.expectOne((r) => r.url === '/api/v1/me/sessions' && r.params.get('date') === date);

  it("asks for the school's date, not the browser's", async () => {
    // The browser itself still thinks it is the 14th.
    expect(new Date().getDate()).toBe(14);

    sessionsFor('2027-01-15').flush([]);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('15.01.2027');
  });

  it('moves to the new day when the window regains focus after midnight', async () => {
    sessionsFor('2027-01-15').flush([]);
    await settle(fixture);

    // 21:05 UTC on the 15th is 00:05 on the 16th in Istanbul.
    vi.setSystemTime(new Date('2027-01-15T21:05:00Z'));
    window.dispatchEvent(new Event('focus'));
    await settle(fixture);

    sessionsFor('2027-01-16').flush([]);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('16.01.2027');
  });
});
