import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { LucideConstruction } from '@lucide/angular';
import { map } from 'rxjs';

import { ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

/** Stand-in for menu entries whose feature has not landed yet; the route data carries the title. */
@Component({
  selector: 'app-placeholder-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent, LucideConstruction],
  template: `
    <app-page-header [title]="title() ?? ''" />
    <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
      <div class="grid size-12 place-items-center rounded-2xl bg-muted text-muted-foreground">
        <svg lucideConstruction class="size-6"></svg>
      </div>
      <p class="text-sm text-muted-foreground">{{ text.comingSoon }}</p>
    </div>
  `,
})
export class PlaceholderPage {
  protected readonly text = ui.common;
  protected readonly title = toSignal(
    inject(ActivatedRoute).data.pipe(map((data) => data['title'] as string)),
  );
}
