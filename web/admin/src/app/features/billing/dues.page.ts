import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideReceipt } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1DuesOutstandingGet, apiV1GroupsGet } from '../../api/functions';
import { NamedReferenceDto, OutstandingDueDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { interpolate, ui } from '../../core/i18n/tr';
import { displayMoney, sumAmounts } from '../../core/money/money';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { RECORD_PAYMENT_TITLE_ID, RecordPaymentData, RecordPaymentDialog } from './billing-dialogs';
import { periodLabel } from './billing-format';

interface Totals {
  count: number;
  fee: string;
  paid: string;
  remaining: string;
}

/** Totals walk every page of the current filter, 100 rows at a time (the API maximum). */
const TOTALS_PAGE_SIZE = 100;

/** US-18: outstanding dues by group and period, with exact totals and "take payment". */
@Component({
  selector: 'app-dues-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PageHeaderComponent, PaginationComponent, LucideReceipt],
  template: `
    <app-page-header [title]="text.dues" [description]="text.duesDescription" />

    <div class="card mb-4 grid gap-3 p-4 sm:grid-cols-2" role="search">
      <div class="min-w-0">
        <label class="field-label" for="dues-group">{{ text.group }}</label>
        <select
          id="dues-group"
          class="input"
          (change)="setFilter('group', $any($event.target).value)"
        >
          <option value="">{{ text.allGroups }}</option>
          @for (g of groups(); track g.id) {
            <option [value]="g.id">{{ g.name }}</option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="dues-period">{{ text.period }}</label>
        <input
          id="dues-period"
          type="month"
          class="input"
          [value]="period()"
          (change)="setFilter('period', $any($event.target).value)"
        />
      </div>
    </div>

    <section class="card mb-4 p-4" aria-labelledby="dues-totals-title" aria-live="polite">
      <h2 id="dues-totals-title" class="sr-only">{{ text.dues }}</h2>
      @if (totals(); as t) {
        <dl class="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
          <div>
            <dt class="text-xs text-muted-foreground">{{ totalsLabel(t.count) }}</dt>
            <dd
              class="font-mono text-base font-bold text-destructive"
              data-testid="total-remaining"
            >
              {{ show(t.remaining) }}
            </dd>
          </div>
          <div>
            <dt class="text-xs text-muted-foreground">{{ text.fee }}</dt>
            <dd class="font-mono" data-testid="total-fee">{{ show(t.fee) }}</dd>
          </div>
          <div>
            <dt class="text-xs text-muted-foreground">{{ text.paid }}</dt>
            <dd class="font-mono" data-testid="total-paid">{{ show(t.paid) }}</dd>
          </div>
        </dl>
      } @else if (totalsError(); as message) {
        <p class="flex items-center gap-2 text-sm text-destructive">
          {{ message }}
          <button type="button" class="btn btn-ghost btn-sm" (click)="loadTotals()">
            {{ common.retry }}
          </button>
        </p>
      } @else {
        <p class="text-sm text-muted-foreground">{{ text.totalsLoading }}</p>
      }
    </section>

    @if (list.error(); as message) {
      <div
        role="alert"
        class="card mb-4 flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="list.load()">
          {{ common.retry }}
        </button>
      </div>
    }

    @if (list.loading()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else if (list.isEmpty()) {
      <div class="card flex flex-col items-center gap-3 px-6 py-14 text-center">
        <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideReceipt class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.noDues }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell"
        tabindex="0"
        role="region"
        aria-labelledby="dues-caption"
        [attr.aria-busy]="list.refreshing()"
        [class.opacity-60]="list.refreshing()"
      >
        <table class="data-table min-w-[48rem]">
          <caption id="dues-caption" class="sr-only">
            {{
              text.duesCaption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.student }}</th>
              <th scope="col">{{ text.group }}</th>
              <th scope="col">{{ text.period }}</th>
              <th scope="col" class="text-right">{{ text.fee }}</th>
              <th scope="col" class="text-right">{{ text.paid }}</th>
              <th scope="col" class="text-right">{{ text.remaining }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (d of list.items(); track d.enrollmentId + d.period) {
              <tr>
                <th scope="row">
                  <a
                    class="text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/students', d.studentId]"
                    >{{ d.studentFirstName }} {{ d.studentLastName }}</a
                  >
                </th>
                <td>{{ d.groupName }}</td>
                <td>{{ periodText(d.period) }}</td>
                <td class="text-right font-mono whitespace-nowrap">{{ show(d.fee.amount) }}</td>
                <td class="text-right font-mono whitespace-nowrap">{{ show(d.paid.amount) }}</td>
                <td class="text-right font-mono font-semibold whitespace-nowrap text-destructive">
                  {{ show(d.remaining.amount) }}
                </td>
                <td class="text-right">
                  <button
                    type="button"
                    class="btn btn-secondary btn-sm"
                    [attr.aria-label]="
                      text.takePayment +
                      ': ' +
                      d.studentFirstName +
                      ' ' +
                      d.studentLastName +
                      ', ' +
                      periodText(d.period)
                    "
                    [disabled]="busy()"
                    (click)="takePayment(d)"
                  >
                    {{ text.takePayment }}
                  </button>
                </td>
              </tr>
            }
          </tbody>
        </table>
        <app-pagination
          [page]="list.page()"
          [totalPages]="list.totalPages()"
          [totalCount]="list.totalCount()"
          [disabled]="list.refreshing()"
          (pageChange)="list.goTo($event)"
        />
      </div>
    }
  `,
})
export class DuesPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.billing;
  protected readonly table = ui.table;
  protected readonly common = ui.common;
  protected readonly groups = signal<NamedReferenceDto[]>([]);
  protected readonly groupId = signal('');
  protected readonly period = signal('');
  protected readonly busy = signal(false);
  protected readonly totals = signal<Totals | null>(null);
  protected readonly totalsError = signal<string | null>(null);
  protected readonly list = new PagedList<OutstandingDueDto>((page) =>
    this.api.invoke(apiV1DuesOutstandingGet, {
      groupId: this.groupId() || undefined,
      period: this.period() || undefined,
      page,
      pageSize: 25,
    }),
  );
  private totalsSequence = 0;

  constructor() {
    void this.refresh();
    void this.loadGroups();
  }

  protected periodText(value: string): string {
    return periodLabel(value);
  }

  protected show(amount: string): string {
    return displayMoney(amount);
  }

  protected totalsLabel(count: number): string {
    return interpolate(this.text.totals, { count });
  }

  protected setFilter(filter: 'group' | 'period', value: string): void {
    if (filter === 'group') {
      this.groupId.set(value);
    } else {
      this.period.set(value);
    }
    void this.refresh(true);
  }

  protected async takePayment(due: OutstandingDueDto): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const data: RecordPaymentData = {
        prefill: {
          enrollmentId: due.enrollmentId,
          label: `${due.studentFirstName} ${due.studentLastName} · ${due.groupName}`,
          period: due.period,
          amount: due.remaining.amount,
        },
      };
      const ref = this.dialog.open(RecordPaymentDialog, {
        data,
        ariaLabelledBy: RECORD_PAYMENT_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.paymentRecorded);
      }
      await this.refresh();
    } finally {
      this.busy.set(false);
    }
  }

  /** Sums every outstanding due of the current filter exactly (integer kuruş). */
  async loadTotals(): Promise<void> {
    const request = ++this.totalsSequence;
    this.totals.set(null);
    this.totalsError.set(null);
    try {
      const all: OutstandingDueDto[] = [];
      let page = 1;
      let totalPages = 1;
      do {
        const result = await this.api.invoke(apiV1DuesOutstandingGet, {
          groupId: this.groupId() || undefined,
          period: this.period() || undefined,
          page,
          pageSize: TOTALS_PAGE_SIZE,
        });
        if (request !== this.totalsSequence) {
          return;
        }
        all.push(...result.items);
        totalPages = Number(result.totalPages);
        page++;
      } while (page <= totalPages);
      this.totals.set({
        count: all.length,
        fee: sumAmounts(all.map((d) => d.fee.amount)),
        paid: sumAmounts(all.map((d) => d.paid.amount)),
        remaining: sumAmounts(all.map((d) => d.remaining.amount)),
      });
    } catch (err) {
      if (request === this.totalsSequence) {
        this.totalsError.set(problemMessage(toProblem(err)));
      }
    }
  }

  private async refresh(reset = false): Promise<void> {
    await Promise.all([reset ? this.list.reset() : this.list.load(), this.loadTotals()]);
  }

  private async loadGroups(): Promise<void> {
    try {
      const page = await this.api.invoke(apiV1GroupsGet, { page: 1, pageSize: 100 });
      this.groups.set(page.items.map(({ id, name }) => ({ id, name })));
    } catch {
      // The filter is optional; the list still works without it.
    }
  }
}
