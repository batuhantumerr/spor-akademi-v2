import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';

import { Api } from '../../api/api';
import { apiV1SettingsBillingRulesGet, apiV1SettingsBillingRulesPost } from '../../api/functions';
import { BillingRulesDto } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { displayMoney, fromKurus, toKurus } from '../../core/money/money';
import { SchoolClock } from '../../core/time/school-clock';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';
import { periodLabel } from '../billing/billing-format';

type Field = 'effectiveFrom' | 'secondChildPercent' | 'thirdAndLaterChildPercent';

/** The fee each of three siblings pays in the example, list fees as API text. */
const EXAMPLE_FEES = ['1000.00', '800.00', '600.00'] as const;

/**
 * The fee less a whole percent, rounded to kuruş half away from zero, like the API (DEC-371). Integer arithmetic on
 * kuruş, so the example never shows a floating point artefact.
 */
export function discounted(amount: string, percent: number): string {
  const kurus = toKurus(amount);
  return fromKurus((kurus * BigInt(100 - percent) + 50n) / 100n);
}

/**
 * P2-F8: the school's fee rules (DEC-370): versions from a month on, a sibling discount and a single charge on a
 * group change. Versions are never edited; a new one starts this month or later and past months keep their bill.
 */
@Component({
  selector: 'app-billing-rules-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />

    <section class="card mb-4 p-4 text-sm" aria-labelledby="rules-how-title">
      <h2 id="rules-how-title" class="mb-2 text-base font-semibold">{{ text.howTitle }}</h2>
      <p class="mb-2">{{ text.siblingsHow }}</p>
      <p class="mb-2 text-muted-foreground" aria-live="polite" data-testid="rules-example">
        {{ example() }}
      </p>
      <p>{{ text.groupChangeHow }}</p>
    </section>

    <form
      class="card mb-4 flex flex-col gap-4 p-4"
      aria-labelledby="rules-form-title"
      (submit)="$event.preventDefault(); save()"
      novalidate
    >
      <h2 id="rules-form-title" class="text-base font-semibold">{{ text.formTitle }}</h2>
      <div class="grid gap-4 sm:grid-cols-3">
        <div>
          <label class="field-label" for="rules-from">{{ text.effectiveFrom }}</label>
          <input
            id="rules-from"
            type="month"
            class="input"
            [min]="thisMonth()"
            [value]="effectiveFrom()"
            (input)="effectiveFrom.set($any($event.target).value); clear('effectiveFrom')"
            [attr.aria-invalid]="!!errors()['effectiveFrom']"
            [attr.aria-describedby]="
              'rules-from-hint' + (errors()['effectiveFrom'] ? ' rules-from-error' : '')
            "
          />
          <p id="rules-from-hint" class="mt-1.5 text-xs text-muted-foreground">
            {{ text.effectiveFromHint }}
          </p>
          @if (errors()['effectiveFrom']; as m) {
            <p id="rules-from-error" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" for="rules-second">{{ text.second }}</label>
          <input
            id="rules-second"
            type="number"
            inputmode="numeric"
            class="input max-w-32 font-mono"
            min="0"
            max="100"
            step="1"
            [value]="second()"
            (input)="second.set($any($event.target).value); clear('secondChildPercent')"
            [attr.aria-invalid]="!!errors()['secondChildPercent']"
            [attr.aria-describedby]="
              'rules-percent-hint' + (errors()['secondChildPercent'] ? ' rules-second-error' : '')
            "
          />
          @if (errors()['secondChildPercent']; as m) {
            <p id="rules-second-error" class="field-error">{{ m }}</p>
          }
        </div>
        <div>
          <label class="field-label" for="rules-third">{{ text.third }}</label>
          <input
            id="rules-third"
            type="number"
            inputmode="numeric"
            class="input max-w-32 font-mono"
            min="0"
            max="100"
            step="1"
            [value]="third()"
            (input)="third.set($any($event.target).value); clear('thirdAndLaterChildPercent')"
            [attr.aria-invalid]="!!errors()['thirdAndLaterChildPercent']"
            [attr.aria-describedby]="
              'rules-percent-hint' +
              (errors()['thirdAndLaterChildPercent'] ? ' rules-third-error' : '')
            "
          />
          @if (errors()['thirdAndLaterChildPercent']; as m) {
            <p id="rules-third-error" class="field-error">{{ m }}</p>
          }
        </div>
      </div>
      <p id="rules-percent-hint" class="-mt-2 text-xs text-muted-foreground">
        {{ text.percentHint }}
      </p>
      <div class="flex items-start gap-2">
        <input
          id="rules-single-charge"
          type="checkbox"
          class="mt-1"
          [checked]="singleCharge()"
          (change)="singleCharge.set($any($event.target).checked)"
          aria-describedby="rules-single-charge-hint"
        />
        <div>
          <label class="text-sm font-medium" for="rules-single-charge">{{
            text.singleCharge
          }}</label>
          <p id="rules-single-charge-hint" class="text-xs text-muted-foreground">
            {{ text.singleChargeHint }}
          </p>
        </div>
      </div>
      @if (error()) {
        <p role="alert" class="text-sm text-destructive">{{ error() }}</p>
      }
      <div>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ saving() ? text.saving : text.save }}
        </button>
      </div>
    </form>

    <section class="card p-4" aria-labelledby="rules-history-title">
      <h2 id="rules-history-title" class="mb-3 text-base font-semibold">
        {{ text.historyTitle }}
      </h2>
      @if (loadError(); as message) {
        <div role="alert" class="flex items-center justify-between gap-3 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (versions() === null) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (versions()!.length === 0) {
        <p class="text-sm text-muted-foreground">{{ text.none }}</p>
      } @else {
        <div class="table-shell">
          <table class="data-table min-w-[32rem]">
            <caption class="sr-only">
              {{
                text.historyCaption
              }}
            </caption>
            <thead>
              <tr>
                <th scope="col">{{ text.from }}</th>
                <th scope="col" class="text-right">{{ text.secondShort }}</th>
                <th scope="col" class="text-right">{{ text.thirdShort }}</th>
                <th scope="col">{{ text.groupChange }}</th>
              </tr>
            </thead>
            <tbody>
              @for (v of versions(); track v.id) {
                <tr>
                  <th scope="row" class="font-normal">
                    {{ period(v.effectiveFrom) }}
                    @if (v.effectiveFrom === currentFrom()) {
                      <span class="badge badge-success ml-1">{{ text.current }}</span>
                    } @else if (v.effectiveFrom > thisMonth()) {
                      <span class="badge badge-info ml-1">{{ text.upcoming }}</span>
                    }
                  </th>
                  <td class="text-right font-mono">{{ percent(v.secondChildPercent) }}</td>
                  <td class="text-right font-mono">{{ percent(v.thirdAndLaterChildPercent) }}</td>
                  <td>
                    {{ v.singleChargeOnGroupChange ? text.singleChargeOn : text.singleChargeOff }}
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class BillingRulesPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly clock = inject(SchoolClock);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly text = ui.billingRules;
  protected readonly common = ui.common;
  protected readonly thisMonth = computed(() => this.clock.today().slice(0, 7));
  protected readonly effectiveFrom = signal(this.clock.today().slice(0, 7));
  /** Suggested defaults (DEC-371): 10 % for the second child, 15 % for the third and later. */
  protected readonly second = signal('10');
  protected readonly third = signal('15');
  protected readonly singleCharge = signal(true);
  protected readonly versions = signal<BillingRulesDto[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Partial<Record<Field, string>>>({});
  /** The version in force this month: the newest one that started on or before it (the API lists newest first). */
  protected readonly currentFrom = computed(
    () =>
      (this.versions() ?? []).find((v) => v.effectiveFrom <= this.thisMonth())?.effectiveFrom ??
      null,
  );
  /** The three-sibling example with the percents in the form (or 0 while a field is not a valid percent). */
  protected readonly example = computed(() => {
    const second = BillingRulesPage.percentOf(this.second()) ?? 0;
    const third = BillingRulesPage.percentOf(this.third()) ?? 0;
    const [a, b, c] = EXAMPLE_FEES;
    return interpolate(this.text.example, {
      second,
      third,
      a: displayMoney(a),
      b: displayMoney(b),
      c: displayMoney(c),
      b2: displayMoney(discounted(b, second)),
      c2: displayMoney(discounted(c, third)),
    });
  });

  constructor() {
    void this.load();
  }

  /** A whole percent from 0 to 100, or null. */
  static percentOf(text: string): number | null {
    const trimmed = String(text).trim();
    if (!/^\d{1,3}$/.test(trimmed)) {
      return null;
    }
    const value = Number(trimmed);
    return value <= 100 ? value : null;
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.versions.set(await this.api.invoke(apiV1SettingsBillingRulesGet));
    } catch (err) {
      this.loadError.set(problemMessage(toProblem(err)));
    }
  }

  protected period(value: string): string {
    return periodLabel(value);
  }

  protected percent(value: number | string): string {
    return interpolate(this.text.percent, { value: Number(value) });
  }

  protected clear(field: Field): void {
    if (this.errors()[field]) {
      this.errors.update((all) => ({ ...all, [field]: undefined }));
    }
  }

  protected async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const from = this.effectiveFrom();
    const second = BillingRulesPage.percentOf(this.second());
    const third = BillingRulesPage.percentOf(this.third());
    const problems: Partial<Record<Field, string>> = {};
    if (!/^\d{4}-\d{2}$/.test(from)) {
      problems.effectiveFrom = errorMessages['Field.Required'];
    } else if (from < this.thisMonth()) {
      problems.effectiveFrom = errorMessages['BillingRules.PeriodInPast'];
    }
    if (second === null) {
      problems.secondChildPercent = errorMessages['BillingRules.InvalidPercent'];
    }
    if (third === null) {
      problems.thirdAndLaterChildPercent = errorMessages['BillingRules.InvalidPercent'];
    }
    if (Object.keys(problems).length > 0) {
      this.errors.set(problems);
      this.focusFirstInvalid();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.set({});
    try {
      const versions = await this.api.invoke(apiV1SettingsBillingRulesPost, {
        body: {
          effectiveFrom: from,
          secondChildPercent: second!,
          thirdAndLaterChildPercent: third!,
          singleChargeOnGroupChange: this.singleCharge(),
        },
      });
      this.versions.set(versions);
      this.toasts.success(this.text.saved);
    } catch (err) {
      const problem = toProblem(err);
      const fields = fieldErrors(problem);
      if (
        problem.code === 'BillingRules.PeriodInPast' ||
        problem.code === 'BillingRules.AlreadyDefined'
      ) {
        this.errors.set({ effectiveFrom: problemMessage(problem) });
        this.focusFirstInvalid();
      } else if (
        fields['effectiveFrom'] ||
        fields['secondChildPercent'] ||
        fields['thirdAndLaterChildPercent']
      ) {
        this.errors.set({
          effectiveFrom: fields['effectiveFrom'],
          secondChildPercent: fields['secondChildPercent'],
          thirdAndLaterChildPercent: fields['thirdAndLaterChildPercent'],
        });
        this.focusFirstInvalid();
      } else {
        this.error.set(problemMessage(problem));
      }
    } finally {
      this.saving.set(false);
    }
  }

  private focusFirstInvalid(): void {
    afterNextRender(
      () => {
        const errors = this.errors();
        const id = errors.effectiveFrom
          ? 'rules-from'
          : errors.secondChildPercent
            ? 'rules-second'
            : errors.thirdAndLaterChildPercent
              ? 'rules-third'
              : null;
        if (id) {
          this.host.nativeElement.querySelector<HTMLElement>(`#${id}`)?.focus();
        }
      },
      { injector: this.injector },
    );
  }
}
