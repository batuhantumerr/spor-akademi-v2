import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ui } from '../../core/i18n/tr';
import { CoachRosterPage } from './coach-roster.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('CoachRosterPage', () => {
  let fixture: ComponentFixture<CoachRosterPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [CoachRosterPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(CoachRosterPage);
    fixture.componentRef.setInput('id', 'g1');
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  it('lists names and birth years with a tappable emergency phone', async () => {
    backend.expectOne('/api/v1/groups/g1/roster').flush([
      {
        firstName: 'Ayşe',
        lastName: 'Yılmaz',
        birthYear: 2014,
        emergencyContactPhone: '+905331112233',
      },
      { firstName: 'Can', lastName: 'Er', birthYear: 2015, emergencyContactPhone: '+905321234567' },
    ]);
    backend.expectOne('/api/v1/me/groups').flush([{ id: 'g1', name: 'U12 Mavi' }]);
    await settle(fixture);

    expect(root.querySelector('h1')?.textContent).toContain('U12 Mavi');
    expect(root.textContent).toContain('Ayşe Yılmaz');
    expect(root.textContent).toContain('Doğum yılı 2014');
    const links = root.querySelectorAll<HTMLAnchorElement>('a[href^="tel:"]');
    expect(links.length).toBe(2);
    expect(links[0].getAttribute('href')).toBe('tel:+905331112233');
    expect(links[0].textContent).toContain('0533 111 22 33');
    expect(links[0].getAttribute('aria-label')).toContain('Ayşe Yılmaz');
    expect(links[0].getAttribute('aria-label')).toContain('Acil durum kişisini ara');
    // Large touch target.
    expect(links[0].classList.contains('h-12')).toBe(true);
    // Nothing beyond the roster: no student links or actions.
    expect(root.querySelectorAll('button').length).toBe(0);
  });

  it('shows only the four roster fields even if the API sends more', async () => {
    backend.expectOne('/api/v1/groups/g1/roster').flush([
      {
        firstName: 'Ayşe',
        lastName: 'Yılmaz',
        birthYear: 2014,
        emergencyContactPhone: '+905331112233',
        id: 'st1',
        email: 'gizli@example.test',
        guardianName: 'Gizli Veli',
        notes: 'Gizli not',
      },
    ]);
    backend.expectOne('/api/v1/me/groups').flush([]);
    await settle(fixture);

    const text = root.textContent ?? '';
    expect(text).toContain('Ayşe Yılmaz');
    expect(text).not.toContain('gizli@example.test');
    expect(text).not.toContain('Gizli Veli');
    expect(text).not.toContain('Gizli not');
    expect(root.querySelectorAll('a[href^="tel:"]').length).toBe(1);
    expect(root.querySelectorAll('a[href^="/students"]').length).toBe(0);
  });

  it('tells the coach when the group does not exist', async () => {
    backend
      .expectOne('/api/v1/groups/g1/roster')
      .flush({ code: 'Group.NotFound' }, { status: 404, statusText: 'Not Found' });
    backend.expectOne('/api/v1/me/groups').flush([]);
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(ui.roster.notFound);
  });

  it('tells the coach when the group is not theirs', async () => {
    backend
      .expectOne('/api/v1/groups/g1/roster')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    backend.expectOne('/api/v1/me/groups').flush([]);
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(ui.roster.notFound);
  });
});
