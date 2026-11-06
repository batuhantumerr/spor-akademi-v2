import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';

import { Api } from '../../api/api';
import { apiV1GroupsIdFeesGet, apiV1GroupsIdFeesPost } from '../../api/functions';
import { GroupDto, GroupFeeDto } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';
import { displayMoney, isPositive, money, parseMoneyInput } from '../../core/money/money';
import { SchoolClock } from '../../core/time/school-clock';
import { ToastService } from '../../shared/ui/toast.service';
import { periodLabel } from './billing-format';

type Field = 'effectiveFrom' | 'amount';

/** US-16: a group's monthly fee history and defining the fee from this month on. */
@Component({
  selector: 'app-group-fees',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card p-4" aria-labelledby="group-fees-title">
      <h2 id="group-fees-title" class="mb-3 text-base font-semibold">{{ text.fees }}</h2>

      @if (group().status === 'Active') {
        <form
          class="mb-4 grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-start"
          (submit)="$event.preventDefault(); define()"
          novalidate
        >
          <div>
            <label class="field-label" for="fee-from">{{ text.effectiveFrom }}</label>
            <input
              id="fee-from"
              type="month"
              class="input"
              [min]="thisMonth()"
              [value]="effectiveFrom()"
              (input)="effectiveFrom.set($any($event.target).value); clear('effectiveFrom')"
              [attr.aria-invalid]="!!errors()['effectiveFrom']"
              [attr.aria-describedby]="
                'fee-from-hint' + (errors()['effectiveFrom'] ? ' fee-from-error' : '')
              "
            />
            <p id="fee-from-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.effectiveFromHint }}
            </p>
            @if (errors()['effectiveFrom']; as m) {
              <p id="fee-from-error" class="field-error">{{ m }}</p>
            }
          </div>
          <div>
            <label class="field-label" for="fee-amount">{{ text.amount }}</label>
            <input
              id="fee-amount"
              class="input font-mono"
              inputmode="decimal"
              autocomplete="off"
              [value]="amountText()"
              (input)="amountText.set($any($event.target).value); clear('amount')"
              [attr.aria-invalid]="!!errors()['amount']"
              [attr.aria-describedby]="
                'fee-amount-hint' + (errors()['amount'] ? ' fee-amount-error' : '')
              "
            />
            <p id="fee-amount-hint" class="mt-1.5 text-xs text-muted-foreground">
              {{ text.amountHint }}
            </p>
            @if (errors()['amount']; as m) {
              <p id="fee-amount-error" class="field-error">{{ m }}</p>
            }
          </div>
          <button type="submit" class="btn btn-primary sm:mt-6" [disabled]="saving()">
            {{ text.defineFee }}
          </button>
        </form>
        @if (error()) {
          <p role="alert" class="mb-3 text-sm text-destructive">{{ error() }}</p>
        }
      }

      <h3 class="mb-1 text-xs font-bold tracking-wider text-muted-foreground uppercase">
        {{ text.feeHistory }}
      </h3>
      @if (loadError(); as message) {
        <div role="alert" class="flex items-center justify-between gap-3 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (fees() === null) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (fees()!.length === 0) {
        <p class="text-sm text-muted-foreground">{{ text.noFees }}</p>
      } @else {
        <ul class="divide-y divide-border/60">
          @for (fee of sortedFees(); track fee.effectiveFrom; let first = $first) {
            <li class="flex items-center justify-between gap-3 py-2 text-sm">
              <span>
                {{ period(fee.effectiveFrom) }}
                @if (first) {
                  <span class="badge badge-success ml-1">{{ text.currentFee }}</span>
                }
              </span>
              <span class="font-mono font-semibold">{{ show(fee.amount.amount) }}</span>
            </li>
          }
        </ul>
      }
    </section>
  `,
})
export class GroupFeesComponent {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly clock = inject(SchoolClock);

  readonly group = input.required<GroupDto>();

  protected readonly text = ui.billing;
  protected readonly common = ui.common;
  protected readonly thisMonth = computed(() => this.clock.today().slice(0, 7));
  protected readonly effectiveFrom = signal(this.clock.today().slice(0, 7));
  protected readonly amountText = signal('');
  protected readonly fees = signal<GroupFeeDto[] | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly errors = signal<Partial<Record<Field, string>>>({});
  /** Newest period first. */
  protected readonly sortedFees = computed(() =>
    [...(this.fees() ?? [])].sort((a, b) => b.effectiveFrom.localeCompare(a.effectiveFrom)),
  );
  private readonly groupId = computed(() => this.group().id);
  private sequence = 0;

  constructor() {
    effect(() => {
      this.groupId();
      untracked(() => void this.load());
    });
  }

  protected period(value: string): string {
    return periodLabel(value);
  }

  protected show(amount: string): string {
    return displayMoney(amount);
  }

  protected clear(field: Field): void {
    if (this.errors()[field]) {
      this.errors.update((all) => ({ ...all, [field]: undefined }));
    }
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.loadError.set(null);
    try {
      const fees = await this.api.invoke(apiV1GroupsIdFeesGet, { id: this.group().id });
      if (request === this.sequence) {
        this.fees.set(fees);
      }
    } catch (err) {
      if (request === this.sequence) {
        this.loadError.set(problemMessage(toProblem(err)));
      }
    }
  }

  protected async define(): Promise<void> {
    if (this.saving()) {
      return;
    }
    const amount = parseMoneyInput(this.amountText());
    const from = this.effectiveFrom();
    const problems: Partial<Record<Field, string>> = {};
    if (!/^\d{4}-\d{2}$/.test(from)) {
      problems.effectiveFrom = errorMessages['Field.Required'];
    } else if (from < this.thisMonth()) {
      problems.effectiveFrom = errorMessages['Fee.PeriodInPast'];
    }
    if (!amount || !isPositive(amount)) {
      problems.amount = this.amountText().trim()
        ? errorMessages['Money.Invalid']
        : errorMessages['Field.Required'];
    }
    if (Object.keys(problems).length > 0) {
      this.errors.set(problems);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.errors.set({});
    try {
      await this.api.invoke(apiV1GroupsIdFeesPost, {
        id: this.group().id,
        body: { effectiveFrom: from, amount: money(amount!) },
      });
      this.amountText.set('');
      this.toasts.success(this.text.feeDefined);
      await this.load();
    } catch (err) {
      const problem = toProblem(err);
      const fields = fieldErrors(problem);
      if (problem.code === 'Fee.PeriodInPast' || problem.code === 'Fee.AlreadyDefined') {
        this.errors.set({ effectiveFrom: problemMessage(problem) });
      } else if (fields['effectiveFrom'] || fields['amount'] || fields['amount.amount']) {
        this.errors.set({
          effectiveFrom: fields['effectiveFrom'],
          amount: fields['amount'] ?? fields['amount.amount'],
        });
      } else {
        this.error.set(problemMessage(problem));
      }
    } finally {
      this.saving.set(false);
    }
  }
}
