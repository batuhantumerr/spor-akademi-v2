import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';

import { ui } from '../../core/i18n/tr';

@Component({
  selector: 'app-not-found-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <main class="flex min-h-dvh flex-col items-center justify-center gap-4 p-6 text-center">
      <p class="font-mono text-6xl font-extrabold text-primary">404</p>
      <h1 class="text-xl font-bold">{{ text.notFoundTitle }}</h1>
      <p class="max-w-sm text-sm text-muted-foreground">{{ text.notFoundText }}</p>
      <a routerLink="/" class="btn btn-primary">{{ text.backHome }}</a>
    </main>
  `,
})
export class NotFoundPage {
  protected readonly text = ui.common;
}
