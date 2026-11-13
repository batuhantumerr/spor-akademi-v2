import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';

import { navigationFor } from '../../layout/navigation';
import { AuthService, CurrentUser } from '../auth/auth.service';
import { ui } from '../i18n/tr';
import { FeatureService, featureGuard } from './feature.service';

const admin: CurrentUser = {
  id: 'u1',
  email: 'mudur@example.test',
  firstName: 'Selin',
  lastName: 'Aksoy',
  role: 'SchoolAdmin',
  tenantId: 's1',
  mustChangePassword: false,
};

describe('FeatureService (P4-10)', () => {
  let backend: HttpTestingController;
  const user = signal<CurrentUser | null>(admin);

  beforeEach(() => {
    user.set(admin);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            user,
            role: () => user()?.role ?? null,
            homeFor: () => '/dashboard',
          },
        },
      ],
    });
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  async function loaded(enabled: string[]): Promise<FeatureService> {
    const features = TestBed.inject(FeatureService);
    TestBed.tick();
    backend.expectOne('/api/v1/features').flush({ enabled });
    await Promise.resolve();
    await new Promise((resolve) => setTimeout(resolve));
    return features;
  }

  it('reads the school’s enabled flags once per session', async () => {
    const features = await loaded(['reports']);

    expect(features.isEnabled('reports')).toBe(true);
    expect(features.isEnabled('parent-online-payments')).toBe(false);
    await features.load();
    backend.expectNone('/api/v1/features');
  });

  it('hides flagged menu items until the flags are known, and while they are off', async () => {
    const features = TestBed.inject(FeatureService);
    const menu = () =>
      navigationFor('SchoolAdmin', (key) => features.isEnabled(key)).flatMap((section) =>
        section.items.map((item) => item.label),
      );
    TestBed.tick();
    expect(menu()).not.toContain(ui.nav.reports);

    backend.expectOne('/api/v1/features').flush({ enabled: [] });
    await new Promise((resolve) => setTimeout(resolve));
    expect(menu()).not.toContain(ui.nav.reports);
    expect(menu()).toContain(ui.nav.dashboard);
  });

  it('shows the item when the flag is on', async () => {
    const features = await loaded(['reports']);

    const labels = navigationFor('SchoolAdmin', (key) => features.isEnabled(key)).flatMap(
      (section) => section.items.map((item) => item.label),
    );
    expect(labels).toContain(ui.nav.reports);
  });

  it('forgets the flags when the user signs out, and reloads for the next user', async () => {
    const features = await loaded(['reports']);

    user.set(null);
    TestBed.tick();
    expect(features.isEnabled('reports')).toBe(false);

    user.set({ ...admin, id: 'u2', tenantId: 's2' });
    TestBed.tick();
    backend.expectOne('/api/v1/features').flush({ enabled: [] });
    await new Promise((resolve) => setTimeout(resolve));
    expect(features.isEnabled('reports')).toBe(false);
  });

  it('asks nothing for the Platform Admin, who has no school', () => {
    user.set({ ...admin, role: 'PlatformAdmin', tenantId: null });
    TestBed.inject(FeatureService);
    TestBed.tick();

    backend.expectNone('/api/v1/features');
  });

  it('guards a disabled feature’s page by sending the user home', async () => {
    TestBed.inject(FeatureService);
    TestBed.tick();
    const guard = TestBed.runInInjectionContext(() =>
      featureGuard('reports')({} as never, {} as never),
    ) as Promise<boolean | UrlTree>;
    backend.expectOne('/api/v1/features').flush({ enabled: [] });

    const result = await guard;
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/dashboard');
  });

  it('lets the page open when the flags cannot be loaded, and tries again next time', async () => {
    const features = TestBed.inject(FeatureService);
    TestBed.tick();
    const guard = TestBed.runInInjectionContext(() =>
      featureGuard('reports')({} as never, {} as never),
    ) as Promise<boolean | UrlTree>;
    backend.expectOne('/api/v1/features').error(new ProgressEvent('error'));

    expect(await guard).toBe(true);
    expect(features.isEnabled('reports')).toBe(false);

    const retry = features.load();
    backend.expectOne('/api/v1/features').flush({ enabled: ['reports'] });
    await retry;
    expect(features.isEnabled('reports')).toBe(true);
  });
});
