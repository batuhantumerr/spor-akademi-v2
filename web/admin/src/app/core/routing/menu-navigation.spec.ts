import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  RouteReuseStrategy,
  Router,
  RouterOutlet,
  provideRouter,
  withRouterConfig,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { MENU_NAVIGATION_STATE, MenuNavigationReuseStrategy } from './menu-navigation';

let layoutsCreated = 0;
let pagesCreated = 0;

@Component({ selector: 'app-test-layout', imports: [RouterOutlet], template: '<router-outlet />' })
class TestLayout {
  constructor() {
    layoutsCreated++;
  }
}

@Component({ selector: 'app-test-page', template: 'page' })
class TestPage {
  constructor() {
    pagesCreated++;
  }
}

describe('MenuNavigationReuseStrategy', () => {
  let harness: RouterTestingHarness;
  let router: Router;

  beforeEach(async () => {
    layoutsCreated = 0;
    pagesCreated = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter(
          [
            {
              path: '',
              component: TestLayout,
              children: [
                { path: 'list', component: TestPage },
                { path: 'other', component: TestPage },
              ],
            },
          ],
          withRouterConfig({ onSameUrlNavigation: 'reload' }),
        ),
        { provide: RouteReuseStrategy, useExisting: MenuNavigationReuseStrategy },
      ],
    });
    harness = await RouterTestingHarness.create('/list');
    router = TestBed.inject(Router);
  });

  it('recreates the open page, but not the layout, when its menu link is clicked again', async () => {
    expect(pagesCreated).toBe(1);

    await router.navigateByUrl('/list', { state: MENU_NAVIGATION_STATE });
    harness.detectChanges();

    expect(pagesCreated).toBe(2);
    expect(layoutsCreated).toBe(1);
  });

  it('recreates the page when the menu link drops the query of the open page', async () => {
    await router.navigateByUrl('/list?status=open');
    expect(pagesCreated).toBe(1);

    await router.navigateByUrl('/list', { state: MENU_NAVIGATION_STATE });

    expect(pagesCreated).toBe(2);
    expect(router.url).toBe('/list');
  });

  it('keeps the page for other navigations to the same route', async () => {
    await router.navigateByUrl('/list');
    await router.navigateByUrl('/list?status=open');

    expect(pagesCreated).toBe(1);
    expect(layoutsCreated).toBe(1);
  });

  it('keeps the layout when the menu moves to another page', async () => {
    await router.navigateByUrl('/other', { state: MENU_NAVIGATION_STATE });

    expect(pagesCreated).toBe(2);
    expect(layoutsCreated).toBe(1);
  });
});
