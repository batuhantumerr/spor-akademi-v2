import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import {
  RouteReuseStrategy,
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
  withRouterConfig,
} from '@angular/router';
import { firstValueFrom, of, timeout } from 'rxjs';

import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';
import { LanguageService } from './core/i18n/language.service';
import { AppTitleStrategy } from './core/i18n/title.strategy';
import { RealtimeService } from './core/realtime/realtime.service';
import { MenuNavigationReuseStrategy } from './core/routing/menu-navigation';
import { ThemeService } from './core/theme/theme.service';

/** A hanging refresh call must never keep the app from starting; the user then signs in again. */
const RESTORE_TIMEOUT_MS = 5000;

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    // A menu click on the open page navigates again and recreates the page (MenuNavigationReuseStrategy, #79).
    provideRouter(
      routes,
      withComponentInputBinding(),
      withRouterConfig({ onSameUrlNavigation: 'reload' }),
    ),
    { provide: RouteReuseStrategy, useExisting: MenuNavigationReuseStrategy },
    { provide: TitleStrategy, useClass: AppTitleStrategy },
    provideHttpClient(withInterceptors([authInterceptor])),
    // Apply the theme and the language (<html lang>, P3-10) and restore the session (refresh cookie) before the first route resolves.
    // Real-time notifications follow the session from here on (P3-08).
    provideAppInitializer(() => {
      inject(ThemeService);
      inject(LanguageService);
      inject(RealtimeService).connectWhileSignedIn();
      return firstValueFrom(
        inject(AuthService)
          .restore()
          .pipe(timeout({ first: RESTORE_TIMEOUT_MS, with: () => of(false) })),
      );
    }),
  ],
};
