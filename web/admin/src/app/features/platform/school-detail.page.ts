import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideArrowLeft } from '@lucide/angular';

import { Api } from '../../api/api';
import {
  apiV1PlatformSchoolsIdFeaturesGet,
  apiV1PlatformSchoolsIdFeaturesKeyPut,
  apiV1PlatformSchoolsIdGet,
} from '../../api/functions';
import { SchoolDto, SchoolFeatureDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { interpolate, ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * P4-10: a school as the Platform Admin sees it, with its feature flags. Each flag is a switch; the change applies to
 * this school at once (the API drops the school's cached flags).
 */
@Component({
  selector: 'app-school-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PageHeaderComponent, LucideArrowLeft],
  template: `
    <a routerLink="/platform/schools" class="btn btn-ghost btn-sm mb-2 -ml-2">
      <svg lucideArrowLeft></svg>{{ schools.back }}
    </a>

    @if (loadError(); as message) {
      <div
        role="alert"
        class="card flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="load(id())">
          {{ common.retry }}
        </button>
      </div>
    } @else if (!school()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else {
      @let s = school()!;
      <app-page-header [title]="s.name" [description]="s.slug + ' · ' + s.timeZone" />

      <section class="card p-4" aria-labelledby="features-title">
        <h2 id="features-title" class="mb-1 text-base font-semibold">{{ text.title }}</h2>
        <p class="mb-3 text-xs text-muted-foreground">{{ text.description }}</p>
        <div class="table-shell" tabindex="0" role="region" aria-labelledby="features-caption">
          <table class="data-table min-w-[36rem]">
            <caption id="features-caption" class="sr-only">
              {{
                text.caption
              }}
            </caption>
            <thead>
              <tr>
                <th scope="col">{{ text.feature }}</th>
                <th scope="col">{{ text.state }}</th>
                <th scope="col">{{ text.source }}</th>
              </tr>
            </thead>
            <tbody>
              @for (feature of features(); track feature.key) {
                <tr>
                  <th scope="row" [id]="'feature-' + feature.key">
                    {{ name(feature) }}
                    <span
                      class="block text-xs font-normal text-muted-foreground"
                      [id]="'feature-hint-' + feature.key"
                      >{{ text.hints[feature.key] ?? feature.description }}</span
                    >
                  </th>
                  <td>
                    <label class="inline-flex items-center gap-2">
                      <input
                        type="checkbox"
                        role="switch"
                        class="size-4 accent-[var(--primary)]"
                        [checked]="feature.enabled"
                        [disabled]="saving() === feature.key"
                        [attr.aria-checked]="feature.enabled"
                        [attr.aria-labelledby]="'feature-' + feature.key"
                        [attr.aria-describedby]="'feature-hint-' + feature.key"
                        [attr.data-testid]="'feature-switch-' + feature.key"
                        (change)="toggle(feature, $any($event.target))"
                      />
                      <span aria-hidden="true">{{ feature.enabled ? text.on : text.off }}</span>
                    </label>
                  </td>
                  <td class="text-xs">
                    @if (feature.overridden) {
                      <span class="badge badge-info">{{ text.fromSchool }}</span>
                    } @else {
                      <span class="text-muted-foreground">{{ defaultLabel(feature) }}</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }
  `,
})
export class SchoolDetailPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);

  /** The school id from the route (component input binding). */
  readonly id = input.required<string>();

  protected readonly text = ui.schoolFeatures;
  protected readonly schools = ui.schools;
  protected readonly common = ui.common;
  protected readonly school = signal<SchoolDto | null>(null);
  protected readonly features = signal<SchoolFeatureDto[]>([]);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => void this.load(id));
    });
  }

  async load(id: string): Promise<void> {
    this.loadError.set(null);
    this.school.set(null);
    try {
      const [school, features] = await Promise.all([
        this.api.invoke(apiV1PlatformSchoolsIdGet, { id }),
        this.api.invoke(apiV1PlatformSchoolsIdFeaturesGet, { id }),
      ]);
      this.features.set(features);
      this.school.set(school);
    } catch (err) {
      this.loadError.set(problemMessage(toProblem(err)) || this.text.loadError);
    }
  }

  protected name(feature: SchoolFeatureDto): string {
    return this.text.names[feature.key] ?? feature.key;
  }

  protected defaultLabel(feature: SchoolFeatureDto): string {
    return interpolate(this.text.fromDefault, {
      value: feature.defaultEnabled ? this.text.on : this.text.off,
    });
  }

  protected async toggle(feature: SchoolFeatureDto, input: HTMLInputElement): Promise<void> {
    const enabled = input.checked;
    if (this.saving()) {
      input.checked = feature.enabled;
      return;
    }
    this.saving.set(feature.key);
    try {
      const saved = await this.api.invoke(apiV1PlatformSchoolsIdFeaturesKeyPut, {
        id: this.id(),
        key: feature.key,
        body: { enabled },
      });
      this.features.update((list) => list.map((f) => (f.key === saved.key ? saved : f)));
      this.toasts.success(
        interpolate(this.text.saved, {
          feature: this.name(saved),
          state: (saved.enabled ? this.text.on : this.text.off).toLocaleLowerCase(),
        }),
      );
    } catch (err) {
      // The switch shows the stored value again.
      input.checked = feature.enabled;
      this.toasts.error(problemMessage(toProblem(err)));
    } finally {
      this.saving.set(null);
    }
  }
}
