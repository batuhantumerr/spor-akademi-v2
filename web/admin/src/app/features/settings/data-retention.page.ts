import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';

import { Api } from '../../api/api';
import {
  apiV1SettingsDataRetentionGet,
  apiV1SettingsDataRetentionPut,
  apiV1SettingsDataRetentionRunsGet,
} from '../../api/functions';
import { AnonymizationRunDto, RetentionPolicyDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * P2-F5: the School Admin's data retention setting (DEC-361) and the latest anonymization runs (DEC-364). Only the
 * personal data period is editable; the payment period is a legal minimum and is shown read-only.
 */
@Component({
  selector: 'app-data-retention-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, PageHeaderComponent],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />

    @if (loadError(); as message) {
      <div
        role="alert"
        class="card flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
          {{ common.retry }}
        </button>
      </div>
    } @else if (!policy()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else {
      @let p = policy()!;
      <form
        class="card mb-4 flex flex-col gap-4 p-4"
        [formGroup]="form"
        (ngSubmit)="save()"
        novalidate
      >
        <div class="flex flex-col gap-1">
          <label class="field-label" for="retention-months">{{ text.months }}</label>
          <input
            id="retention-months"
            class="input max-w-40 font-mono"
            type="number"
            inputmode="numeric"
            [min]="p.minPersonalDataMonths"
            [max]="p.maxPersonalDataMonths"
            formControlName="months"
            [attr.aria-invalid]="invalid() ? 'true' : null"
            aria-describedby="retention-months-hint"
          />
          <p
            id="retention-months-hint"
            class="text-xs"
            [class.text-muted-foreground]="!invalid()"
            [class.text-destructive]="invalid()"
          >
            {{ hint(p) }}
          </p>
        </div>
        <div class="text-sm">
          <p class="text-xs text-muted-foreground">{{ text.paymentYears }}</p>
          <p>{{ paymentYears(p) }}</p>
        </div>
        <div>
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            {{ saving() ? text.saving : text.save }}
          </button>
        </div>
      </form>

      <section class="card p-4" aria-labelledby="runs-title">
        <h2 id="runs-title" class="mb-1 text-base font-semibold">{{ text.runsTitle }}</h2>
        <p class="mb-3 text-xs text-muted-foreground">{{ text.runsCaption }}</p>
        @if (runs().length === 0) {
          <p class="text-sm text-muted-foreground">{{ text.noRuns }}</p>
        } @else {
          <div class="table-shell">
            <table class="data-table min-w-[36rem]">
              <caption class="sr-only">
                {{
                  text.runsTitle
                }}
              </caption>
              <thead>
                <tr>
                  <th scope="col">{{ text.startedAt }}</th>
                  <th scope="col">{{ text.cutoff }}</th>
                  <th scope="col">{{ text.anonymized }}</th>
                  <th scope="col">{{ text.skipped }}</th>
                  <th scope="col">{{ text.duration }}</th>
                  <th scope="col">{{ text.status }}</th>
                </tr>
              </thead>
              <tbody>
                @for (run of runs(); track run.id) {
                  <tr>
                    <td class="font-mono text-xs whitespace-nowrap">
                      {{ dateTime(run.startedAt) }}
                    </td>
                    <td class="font-mono text-xs">{{ date(run.cutoff) }}</td>
                    <td class="font-mono">{{ run.anonymizedCount }}</td>
                    <td class="font-mono">{{ run.skippedCount }}</td>
                    <td class="font-mono text-xs">{{ duration(run) }}</td>
                    <td>
                      <span
                        class="badge"
                        [class.badge-success]="run.status === 'Succeeded'"
                        [class.badge-muted]="run.status !== 'Succeeded'"
                        >{{ text.statuses[run.status] }}</span
                      >
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
    }
  `,
})
export class DataRetentionPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.dataRetention;
  protected readonly common = ui.common;
  protected readonly policy = signal<RetentionPolicyDto | null>(null);
  protected readonly runs = signal<AnonymizationRunDto[]>([]);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly invalid = signal(false);
  protected readonly months = new FormControl<number | null>(null, Validators.required);
  protected readonly form = new FormGroup({ months: this.months });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      const [policy, runs] = await Promise.all([
        this.api.invoke(apiV1SettingsDataRetentionGet),
        this.api.invoke(apiV1SettingsDataRetentionRunsGet),
      ]);
      this.show(policy);
      this.runs.set(runs);
    } catch (err) {
      this.loadError.set(problemMessage(toProblem(err)));
    }
  }

  protected hint(p: RetentionPolicyDto): string {
    return interpolate(this.text.monthsHint, {
      min: p.minPersonalDataMonths,
      max: p.maxPersonalDataMonths,
    });
  }

  protected paymentYears(p: RetentionPolicyDto): string {
    return interpolate(this.text.paymentYearsValue, { years: p.paymentYears });
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  /** The run's start in the viewer's local time, e.g. 14.01.2027 05:00. */
  protected dateTime(iso: string): string {
    const at = new Date(iso);
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${pad(at.getDate())}.${pad(at.getMonth() + 1)}.${at.getFullYear()} ${pad(at.getHours())}:${pad(at.getMinutes())}`;
  }

  protected duration(run: AnonymizationRunDto): string {
    return interpolate(this.text.durationValue, { ms: run.durationMs });
  }

  protected async save(): Promise<void> {
    const current = this.policy();
    if (!current || this.saving()) {
      return;
    }
    const months = Number(this.months.value);
    const min = Number(current.minPersonalDataMonths);
    const max = Number(current.maxPersonalDataMonths);
    if (!Number.isInteger(months) || months < min || months > max) {
      this.invalid.set(true);
      return;
    }
    this.invalid.set(false);
    this.saving.set(true);
    try {
      const saved = await this.api.invoke(apiV1SettingsDataRetentionPut, {
        body: { personalDataMonths: months, version: current.version },
      });
      this.show(saved);
      this.toasts.success(this.text.saved);
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict') {
        this.toasts.error(this.text.conflict);
        await this.load();
      } else {
        this.invalid.set(problem.status === 400);
        this.toasts.error(problemMessage(problem));
      }
    } finally {
      this.saving.set(false);
    }
  }

  private show(policy: RetentionPolicyDto): void {
    this.policy.set(policy);
    this.months.setValue(Number(policy.personalDataMonths));
  }
}
