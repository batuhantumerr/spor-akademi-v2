import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { LucideCopy, LucideCreditCard, LucideExternalLink } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1OnlinePaymentsGet } from '../../api/functions';
import { OnlinePaymentDto, OnlinePaymentStatus } from '../../api/models';
import { LOCALE } from '../../core/i18n/locale';
import { interpolate, ui } from '../../core/i18n/tr';
import { displayMoney } from '../../core/money/money';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { periodLabel } from './billing-format';

type StatusFilter = OnlinePaymentStatus | '';

const dateTimeFormatter = new Intl.DateTimeFormat(LOCALE, {
  dateStyle: 'short',
  timeStyle: 'short',
});

const STATUSES: OnlinePaymentStatus[] = ['Pending', 'Succeeded', 'Failed', 'Expired', 'Created'];

/** The checkout link as an absolute URL, so it can be shared (the sandbox provider returns a relative one). */
export function shareableCheckoutUrl(url: string, origin: string): string {
  return new URL(url, origin).toString();
}

/**
 * P2-F1 (DEC-348): online payment attempts, optionally of one due (`?enrollmentId=&period=`), with their status and
 * the checkout link to share while it is open.
 */
@Component({
  selector: 'app-online-payments-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    PaginationComponent,
    LucideCopy,
    LucideCreditCard,
    LucideExternalLink,
  ],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />

    <div class="card mb-4 flex flex-wrap items-end gap-3 p-4" role="search">
      <div class="min-w-48">
        <label class="field-label" for="online-filter-status">{{ billing.status }}</label>
        <select
          id="online-filter-status"
          class="input"
          (change)="setStatus($any($event.target).value)"
        >
          <option value="">{{ billing.allStatuses }}</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ text.statuses[s] }}</option>
          }
        </select>
      </div>
      @if (filteredByDue()) {
        <p class="flex items-center gap-2 text-sm text-muted-foreground">
          {{ text.filteredByDue }}
          <button type="button" class="btn btn-ghost btn-sm" (click)="showAll()">
            {{ text.showAll }}
          </button>
        </p>
      }
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
          <svg lucideCreditCard class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell"
        tabindex="0"
        role="region"
        aria-labelledby="online-caption"
        [attr.aria-busy]="list.refreshing()"
        [class.opacity-60]="list.refreshing()"
      >
        <table class="data-table min-w-[52rem]">
          <caption id="online-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ billing.student }}</th>
              <th scope="col">{{ billing.group }}</th>
              <th scope="col">{{ billing.period }}</th>
              <th scope="col" class="text-right">{{ billing.amount }}</th>
              <th scope="col">{{ billing.status }}</th>
              <th scope="col">{{ text.startedAt }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr [attr.data-testid]="'online-' + p.id">
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
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="p.status === 'Succeeded'"
                    [class.badge-info]="p.status === 'Pending' || p.status === 'Created'"
                    [class.badge-danger]="p.status === 'Failed'"
                    [class.badge-muted]="p.status === 'Expired'"
                    >{{ text.statuses[p.status] }}</span
                  >
                  @if (p.failureCode) {
                    <span class="mt-1 block text-xs text-muted-foreground">{{
                      failure(p.failureCode)
                    }}</span>
                  }
                  @if (p.checkoutUrl && p.checkoutExpiresAt) {
                    <span class="mt-1 block text-xs text-muted-foreground">{{
                      expires(p.checkoutExpiresAt)
                    }}</span>
                  }
                </td>
                <td class="font-mono text-xs whitespace-nowrap">{{ dateTime(p.startedAt) }}</td>
                <td class="text-right whitespace-nowrap">
                  @if (p.checkoutUrl; as url) {
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm"
                      [attr.aria-label]="copyLabel(p)"
                      (click)="copy(url)"
                    >
                      <svg lucideCopy></svg>{{ text.copyLink }}
                    </button>
                    <a
                      class="btn btn-ghost btn-sm"
                      [href]="absolute(url)"
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      <svg lucideExternalLink></svg>{{ text.openLink }}
                    </a>
                  }
                  @if (p.paymentId) {
                    <a class="btn btn-ghost btn-sm" routerLink="/payments">{{
                      text.viewPayment
                    }}</a>
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
export class OnlinePaymentsPage {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly text = ui.onlinePayments;
  protected readonly billing = ui.billing;
  protected readonly table = ui.table;
  protected readonly common = ui.common;
  protected readonly statuses = STATUSES;
  protected readonly status = signal<StatusFilter>('');
  protected readonly enrollmentId = signal(
    this.route.snapshot.queryParamMap.get('enrollmentId') ?? '',
  );
  protected readonly period = signal(this.route.snapshot.queryParamMap.get('period') ?? '');
  protected readonly filteredByDue = computed(() => this.enrollmentId() !== '');
  protected readonly list = new PagedList<OnlinePaymentDto>((page) =>
    this.api.invoke(apiV1OnlinePaymentsGet, {
      enrollmentId: this.enrollmentId() || undefined,
      period: this.period() || undefined,
      status: this.status() || undefined,
      page,
      pageSize: 25,
    }),
  );

  constructor() {
    void this.list.load();
  }

  protected periodText(value: string): string {
    return periodLabel(value);
  }

  protected show(p: OnlinePaymentDto): string {
    return displayMoney(p.amount);
  }

  protected dateTime(iso: string): string {
    return dateTimeFormatter.format(new Date(iso));
  }

  protected expires(iso: string): string {
    return interpolate(this.text.expiresAt, { time: this.dateTime(iso) });
  }

  protected failure(code: string): string {
    return interpolate(this.text.failure, { code });
  }

  protected copyLabel(p: OnlinePaymentDto): string {
    return interpolate(this.text.copyLinkLabel, {
      student: `${p.studentFirstName} ${p.studentLastName}`,
      period: periodLabel(p.period),
    });
  }

  protected absolute(url: string): string {
    return shareableCheckoutUrl(url, window.location.origin);
  }

  protected async copy(url: string): Promise<void> {
    const link = this.absolute(url);
    try {
      await navigator.clipboard.writeText(link);
      this.toasts.success(this.text.linkCopied);
    } catch {
      this.toasts.error(interpolate(this.text.copyFailed, { url: link }));
    }
  }

  protected setStatus(value: string): void {
    this.status.set(value as StatusFilter);
    void this.list.reset();
  }

  protected showAll(): void {
    this.enrollmentId.set('');
    this.period.set('');
    void this.router.navigate([], { queryParams: {} });
    void this.list.reset();
  }
}
