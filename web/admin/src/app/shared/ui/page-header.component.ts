import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Page title, one-line description and an action slot (right on desktop, below on mobile). */
@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mb-6 flex flex-col justify-between gap-4 md:flex-row md:items-center">
      <div>
        <h1 class="mb-1 text-xl font-bold tracking-tight text-foreground">{{ title() }}</h1>
        @if (description()) {
          <p class="text-sm text-muted-foreground">{{ description() }}</p>
        }
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <ng-content />
      </div>
    </div>
  `,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string>();
}
