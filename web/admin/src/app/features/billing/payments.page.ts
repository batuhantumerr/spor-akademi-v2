import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucidePlus, LucideWallet } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1GroupsGet, apiV1PaymentsGet } from '../../api/functions';
import { NamedReferenceDto, PaymentDto, PaymentStatus } from '../../api/models';
import { formatDate } from '../../core/format/format';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { displayMoney } from '../../core/money/money';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import {
  ALREADY_CANCELLED,
  CANCEL_PAYMENT_TITLE_ID,
  CancelPaymentData,
  CancelPaymentDialog,
  CancelPaymentResult,
  RECORD_PAYMENT_TITLE_ID,
  RecordPaymentData,
  RecordPaymentDialog,
} from './billing-dialogs';
import { periodLabel } from './billing-format';

type StatusFilter = PaymentStatus | '';

/** US-17: payments with group / period / status filters; record and cancel. */
@Component({
  selector: 'app-payments-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PageHeaderComponent, PaginationComponent, LucidePlus, LucideWallet],
  template: `
    <app-page-header [title]="text.payments" [description]="text.paymentsDescription">
      <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="record()">
        <svg lucidePlus></svg>{{ text.recordPayment }}
      </button>
    </app-page-header>

    <div class="card mb-4 grid gap-3 p-4 sm:grid-cols-3" role="search">
      <div class="min-w-0">
        <label class="field-label" for="pay-filter-group">{{ text.group }}</label>
        <select
          id="pay-filter-group"
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
        <label class="field-label" for="pay-filter-period">{{ text.period }}</label>
        <input
          id="pay-filter-period"
          type="month"
          class="input"
          [value]="period()"
          (change)="setFilter('period', $any($event.target).value)"
        />
      </div>
      <div class="min-w-0">
        <label class="field-label" for="pay-filter-status">{{ text.status }}</label>
        <select
          id="pay-filter-status"
          class="input"
          (change)="setFilter('status', $any($event.target).value)"
        >
          <option value="">{{ text.allStatuses }}</option>
          <option value="Recorded">{{ text.statuses['Recorded'] }}</option>
          <option value="Cancelled">{{ text.statuses['Cancelled'] }}</option>
        </select>
      </div>
    </div>

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
          <svg lucideWallet class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.noPayments }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell"
        tabindex="0"
        role="region"
        aria-labelledby="payments-caption"
        [attr.aria-busy]="list.refreshing()"
        [class.opacity-60]="list.refreshing()"
      >
        <table class="data-table min-w-[52rem]">
          <caption id="payments-caption" class="sr-only">
            {{
              text.paymentsCaption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.student }}</th>
              <th scope="col">{{ text.group }}</th>
              <th scope="col">{{ text.period }}</th>
              <th scope="col" class="text-right">{{ text.amount }}</th>
              <th scope="col">{{ text.method }}</th>
              <th scope="col">{{ text.paidOn }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr [class.opacity-70]="p.status === 'Cancelled'">
                <th scope="row">
                  <a
                    class="text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/students', p.studentId]"
                    >{{ p.studentFirstName }} {{ p.studentLastName }}</a
                  >
                </th>
                <td>{{ p.groupName }}</td>
                <td>{{ periodText(p.period) }}</td>
                <td class="text-right font-mono whitespace-nowrap">{{ show(p) }}</td>
                <td>{{ text.methods[p.method] }}</td>
                <td class="font-mono text-xs">{{ date(p.paidOn) }}</td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="p.status === 'Recorded'"
                    [class.badge-muted]="p.status !== 'Recorded'"
                    >{{ text.statuses[p.status] }}</span
                  >
                  @if (p.cancelReason) {
                    <span class="mt-1 block text-xs text-muted-foreground">{{
                      cancelledBecause(p.cancelReason)
                    }}</span>
                  }
                </td>
                <td class="text-right">
                  @if (p.status === 'Recorded') {
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm hover:!bg-destructive/10 hover:!text-destructive"
                      [attr.aria-label]="
                        text.cancel + ': ' + p.studentFirstName + ' ' + p.studentLastName
                      "
                      [disabled]="busy()"
                      (click)="cancel(p)"
                    >
                      {{ text.cancel }}
                    </button>
                  }
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
export class PaymentsPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.billing;
  protected readonly table = ui.table;
  protected readonly common = ui.common;
  protected readonly groups = signal<NamedReferenceDto[]>([]);
  protected readonly groupId = signal('');
  protected readonly period = signal('');
  protected readonly status = signal<StatusFilter>('');
  protected readonly busy = signal(false);
  protected readonly list = new PagedList<PaymentDto>((page) =>
    this.api.invoke(apiV1PaymentsGet, {
      groupId: this.groupId() || undefined,
      period: this.period() || undefined,
      status: this.status() || undefined,
      page,
      pageSize: 25,
    }),
  );

  constructor() {
    void this.list.load();
    void this.loadGroups();
  }

  protected periodText(value: string): string {
    return periodLabel(value);
  }

  protected show(p: PaymentDto): string {
    return displayMoney(p.amount);
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected cancelledBecause(reason: string): string {
    return interpolate(this.text.cancelledBecause, { reason });
  }

  protected setFilter(filter: 'group' | 'period' | 'status', value: string): void {
    if (filter === 'group') {
      this.groupId.set(value);
    } else if (filter === 'period') {
      this.period.set(value);
    } else {
      this.status.set(value as StatusFilter);
    }
    void this.list.reset();
  }

  protected async record(): Promise<void> {
    await this.exclusive(async () => {
      const data: RecordPaymentData = {};
      const ref = this.dialog.open<PaymentDto>(RecordPaymentDialog, {
        data,
        ariaLabelledBy: RECORD_PAYMENT_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.paymentRecorded);
        await this.list.load();
      }
    });
  }

  protected async cancel(payment: PaymentDto): Promise<void> {
    await this.exclusive(async () => {
      const data: CancelPaymentData = { payment };
      const ref = this.dialog.open<CancelPaymentResult>(CancelPaymentDialog, {
        data,
        ariaLabelledBy: CANCEL_PAYMENT_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      const result = await firstValueFrom(ref.closed);
      if (result === ALREADY_CANCELLED) {
        this.toasts.error(errorMessages['Payment.AlreadyCancelled']);
      } else if (result) {
        this.toasts.success(this.text.cancelled);
      }
      // Reload either way: someone may have cancelled it first.
      await this.list.load();
    });
  }

  private async loadGroups(): Promise<void> {
    try {
      const page = await this.api.invoke(apiV1GroupsGet, { page: 1, pageSize: 100 });
      this.groups.set(page.items.map(({ id, name }) => ({ id, name })));
    } catch {
      // The filter is optional; the list still works without it.
    }
  }

  private async exclusive(work: () => Promise<void>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await work();
    } finally {
      this.busy.set(false);
    }
  }
}
