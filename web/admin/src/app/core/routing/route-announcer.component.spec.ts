import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, RouterOutlet, provideRouter } from '@angular/router';

import { RouteAnnouncerComponent } from './route-announcer.component';

@Component({
  selector: 'app-test-page',
  template: '<main><h1>Page</h1><input id="filter" /></main>',
})
class TestPage {}

@Component({
  selector: 'app-test-root',
  imports: [RouterOutlet, RouteAnnouncerComponent],
  template: '<button id="menu">menu</button><router-outlet /><app-route-announcer />',
})
class TestRoot {}

describe('RouteAnnouncerComponent (P3-11, DEC-438)', () => {
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'a', component: TestPage },
          { path: 'b', component: TestPage },
        ]),
      ],
    });
    const fixture = TestBed.createComponent(TestRoot);
    document.body.appendChild(fixture.nativeElement);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/a');
    await fixture.whenStable();
  });

  afterEach(() => {
    document.body.innerHTML = '';
  });

  const announcer = () =>
    document.querySelector<HTMLElement>('[data-testid="route-announcer"]')!.textContent!.trim();

  it('leaves the first navigation alone', () => {
    expect(announcer()).toBe('');
    expect(document.activeElement).toBe(document.body);
  });

  it('announces the new title and moves focus to main after a navigation to another path', async () => {
    document.title = 'B · Spor Akademi';
    document.getElementById('menu')!.focus();
    await router.navigateByUrl('/b');
    await TestBed.inject(Router).navigated;
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();

    expect(announcer()).toBe('B · Spor Akademi');
    expect(document.activeElement?.tagName).toBe('MAIN');
    expect(document.querySelector('main')!.getAttribute('tabindex')).toBe('-1');
  });

  it('keeps focus where it is when only the query string changes (list filters)', async () => {
    const filter = document.getElementById('filter') as HTMLInputElement;
    filter.focus();
    await router.navigateByUrl('/a?group=1');
    await new Promise((resolve) => setTimeout(resolve));
    TestBed.tick();

    expect(document.activeElement).toBe(filter);
    expect(announcer()).toBe('');
  });
});
