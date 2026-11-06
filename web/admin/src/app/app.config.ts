import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { firstValueFrom, of, timeout } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { AppTitleStrategy } from './core/i18n/title.strategy';
import { ThemeService } from './core/theme/theme.service';

/** A hanging refresh call must never keep the app from starting; the user then signs in again. */
const RESTORE_TIMEOUT_MS = 5000;

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    { provide: TitleStrategy, useClass: AppTitleStrategy },
    provideHttpClient(withInterceptors([authInterceptor])),
    // Apply the theme and restore the session (refresh cookie) before the first route resolves.
    provideAppInitializer(() => {
      inject(ThemeService);
      return firstValueFrom(
        inject(AuthService)
          .restore()
          .pipe(timeout({ first: RESTORE_TIMEOUT_MS, with: () => of(false) })),
      );
    }),
  ],
};
