import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { LucideExternalLink, LucideListChecks } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1PlatformJobsDashboardSessionPost } from '../../api/functions';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';

/** Where the API serves the Hangfire Dashboard (DEC-332); the dev proxy and the reverse proxy route it to the API. */
export const JOBS_PANEL_URL = '/jobs';

/**
 * P2-F2: the Platform Admin's way into the job panel. The access token lives in memory and cannot follow a
 * navigation, so the page first asks the API for a short-lived panel cookie, then opens the panel in a new tab.
 */
@Component({
  selector: 'app-jobs-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent, LucideExternalLink, LucideListChecks],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />
    <div class="card flex flex-col items-start gap-4 p-6">
      <div class="flex items-start gap-3">
        <div
          class="grid size-10 shrink-0 place-items-center rounded-2xl bg-primary/10 text-primary"
        >
          <svg lucideListChecks class="size-5"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.note }}</p>
      </div>
      <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="open()">
        <svg lucideExternalLink></svg>{{ busy() ? text.opening : text.open }}
      </button>
      @if (ready()) {
        <a class="text-sm underline" [href]="url" target="_blank" rel="noopener noreferrer">{{
          text.fallback
        }}</a>
      }
    </div>
  `,
})
export class JobsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly window = inject(DOCUMENT).defaultView;

  protected readonly text = ui.jobs;
  protected readonly url = JOBS_PANEL_URL;
  protected readonly busy = signal(false);
  protected readonly ready = signal(false);

  protected async open(): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      // Sets the HttpOnly panel cookie; nothing readable comes back.
      await this.api.invoke(apiV1PlatformJobsDashboardSessionPost);
      this.ready.set(true);
      this.window?.open(JOBS_PANEL_URL, '_blank', 'noopener,noreferrer');
      this.toasts.success(this.text.opened);
    } catch (error) {
      this.toasts.error(problemMessage(toProblem(error)));
    } finally {
      this.busy.set(false);
    }
  }
}
