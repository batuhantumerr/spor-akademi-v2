import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import type { EChartsCoreOption } from 'echarts/core';

import { ui } from '../../core/i18n/tr';
import { ChartComponent } from '../../shared/ui/chart.component';

/**
 * A dashboard chart card. The canvas is exposed as one image with a text summary, and the same
 * numbers can be shown as a table (NFR-USA): the toggle keeps the page compact for sighted users
 * while screen reader and keyboard users get the exact values.
 */
@Component({
  selector: 'app-dashboard-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChartComponent],
  template: `
    <section class="card flex h-full min-w-0 flex-col" [attr.aria-labelledby]="key() + '-title'">
      <div class="flex items-start justify-between gap-3 px-4 pt-4">
        <div class="min-w-0">
          <h2 [id]="key() + '-title'" class="text-sm font-semibold text-foreground">
            {{ heading() }}
          </h2>
          @if (subtitle()) {
            <p class="text-xs text-muted-foreground">{{ subtitle() }}</p>
          }
        </div>
        @if (!empty()) {
          <button
            type="button"
            class="btn btn-ghost btn-sm shrink-0"
            [attr.aria-expanded]="tableOpen()"
            [attr.aria-controls]="key() + '-table'"
            (click)="tableOpen.set(!tableOpen())"
          >
            {{ tableOpen() ? text.hideTable : text.showTable }}
          </button>
        }
      </div>
      <div class="flex-1 p-4">
        @if (empty(); as message) {
          <p class="grid h-full min-h-40 place-items-center text-sm text-muted-foreground">
            {{ message }}
          </p>
        } @else {
          <div role="img" [attr.aria-label]="summary()" data-testid="chart-figure">
            <app-chart [options]="options()" [height]="height()" />
          </div>
          <div [id]="key() + '-table'" class="mt-4 overflow-x-auto" [hidden]="!tableOpen()">
            <table class="w-full text-sm">
              <caption class="sr-only">
                {{
                  heading()
                }}
              </caption>
              <thead class="border-b border-border bg-muted/60">
                <tr>
                  @for (column of columns(); track $index) {
                    <th
                      scope="col"
                      class="h-9 px-3 text-left text-xs font-semibold tracking-wider text-muted-foreground uppercase"
                    >
                      {{ column }}
                    </th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track $index) {
                  <tr class="border-b border-border/60">
                    @for (cell of row; track $index) {
                      @if ($first) {
                        <th scope="row" class="px-3 py-2 text-left font-medium">{{ cell }}</th>
                      } @else {
                        <td class="px-3 py-2 font-mono">{{ cell }}</td>
                      }
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </div>
    </section>
  `,
})
export class DashboardChartComponent {
  /** Prefix for element ids. */
  readonly key = input.required<string>();
  readonly heading = input.required<string>();
  readonly subtitle = input<string>();
  readonly summary = input.required<string>();
  readonly options = input.required<EChartsCoreOption>();
  readonly columns = input<string[]>([]);
  readonly rows = input<string[][]>([]);
  readonly height = input(260);
  /** When set, shown instead of the chart. */
  readonly empty = input<string | null>(null);

  protected readonly text = ui.dashboard;
  protected readonly tableOpen = signal(false);
}
