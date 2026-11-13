import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { Api } from '../../api/api';
import { apiV1FeaturesGet } from '../../api/functions';
import { AuthService } from '../auth/auth.service';

/** Feature flag keys the admin panel reads (P4-10); the catalog is in the API (FeatureCatalog). */
export type FeatureKey = 'reports' | 'parent-online-payments';

/**
 * The signed-in school user's enabled feature flags (P4-10, DEC-585). Loaded once per session (the API caches them
 * per school); a flag the Platform Admin changes reaches the menu on the next sign-in or page load, while the API
 * already answers 404 for a disabled feature. Until the list is known, flagged items stay hidden, so a disabled
 * feature never flashes into the menu.
 */
@Injectable({ providedIn: 'root' })
export class FeatureService {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);

  private readonly enabledKeys = signal<ReadonlySet<string> | null>(null);
  private pending: Promise<void> | null = null;
  private session: string | null = null;

  /** Whether the list has been loaded for the current user. */
  readonly loaded = computed(() => this.enabledKeys() !== null);

  constructor() {
    // A new session (or a sign-out) drops the previous user's flags and loads the new user's.
    effect(() => {
      this.auth.user();
      untracked(() => void this.load());
    });
  }

  isEnabled(key: FeatureKey): boolean {
    return this.enabledKeys()?.has(key) ?? false;
  }

  /**
   * Loads the current user's flags once per session; concurrent callers share the request. The Platform Admin has
   * no school flags. A failure leaves flagged items hidden and lets the next caller try again.
   */
  load(): Promise<void> {
    const session = this.sync();
    if (session === null) {
      return Promise.resolve();
    }
    this.pending ??= this.api.invoke(apiV1FeaturesGet).then(
      (features) => {
        if (this.session === session) {
          this.enabledKeys.set(new Set(features.enabled));
        }
      },
      () => {
        if (this.session === session) {
          this.pending = null;
        }
      },
    );
    return this.pending;
  }

  private sync(): string | null {
    const user = this.auth.user();
    const session =
      user && user.role !== 'PlatformAdmin' ? `${user.id}|${user.tenantId ?? ''}` : null;
    if (session !== this.session) {
      this.session = session;
      this.enabledKeys.set(null);
      this.pending = null;
    }
    return session;
  }
}

/**
 * Route guard: a disabled feature's page sends the user to their home page. If the flags could not be loaded (network
 * error), the page is allowed and the API decides: a disabled feature's endpoints answer 404 anyway, and the next
 * navigation tries the flags again.
 */
export function featureGuard(key: FeatureKey): CanActivateFn {
  return async () => {
    const features = inject(FeatureService);
    const auth = inject(AuthService);
    const router = inject(Router);
    await features.load();
    return !features.loaded() || features.isEnabled(key)
      ? true
      : router.parseUrl(auth.homeFor(auth.role()));
  };
}
