import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideScrollText } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1AuditLogActorsGet, apiV1AuditLogGet } from '../../api/functions';
import { AuditActorDto, AuditEntityType, AuditEntryDto } from '../../api/models';
import { absoluteTime } from '../../core/format/relative-time';
import { interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';

export const AUDIT_PAGE_SIZE = 25;

const ENTITY_TYPES: AuditEntityType[] = ['Student', 'Guardian', 'StudentDocument', 'User'];

/**
 * P4-05: the school's personal data audit log (DEC-530 – DEC-537): who read or changed which record, when, and which
 * fields. Values are never stored, so none are shown. Filters: record type, user and a date range (school days).
 */
@Component({
  selector: 'app-audit-log-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PageHeaderComponent, PaginationComponent, LucideScrollText],
  template: `
    <app-page-header [title]="text.title" [description]="text.description" />

    <form
      class="card mb-4 grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-4"
      role="search"
      [attr.aria-label]="text.filters"
      (submit)="$event.preventDefault()"
    >
      <div class="min-w-0">
        <label class="field-label" for="audit-type">{{ text.entityType }}</label>
        <select id="audit-type" class="input" (change)="setType($any($event.target).value)">
          <option value="" [selected]="entityType() === ''">{{ text.allTypes }}</option>
          @for (type of types; track type) {
            <option [value]="type" [selected]="entityType() === type">
              {{ text.entityTypes[type] }}
            </option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="audit-user">{{ text.user }}</label>
        <select id="audit-user" class="input" (change)="setUser($any($event.target).value)">
          <option value="" [selected]="userId() === ''">{{ text.allUsers }}</option>
          @for (actor of actors(); track actor.userId) {
            <option [value]="actor.userId" [selected]="userId() === actor.userId">
              {{ actorLabel(actor) }}
            </option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="audit-from">{{ text.from }}</label>
        <input
          id="audit-from"
          type="date"
          class="input"
          [value]="from()"
          [max]="to() || null"
          [attr.aria-invalid]="rangeInvalid()"
          [attr.aria-describedby]="rangeInvalid() ? 'audit-range-error' : null"
          (change)="setFrom($any($event.target).value)"
        />
      </div>
      <div class="min-w-0">
        <label class="field-label" for="audit-to">{{ text.to }}</label>
        <input
          id="audit-to"
          type="date"
          class="input"
          [value]="to()"
          [min]="from() || null"
          [attr.aria-invalid]="rangeInvalid()"
          [attr.aria-describedby]="rangeInvalid() ? 'audit-range-error' : null"
          (change)="setTo($any($event.target).value)"
        />
      </div>
      @if (rangeInvalid()) {
        <p id="audit-range-error" class="field-error sm:col-span-2 lg:col-span-4">
          {{ text.rangeInvalid }}
        </p>
      }
    </form>

    <p class="sr-only" role="status">{{ resultText() }}</p>

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
      <p class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else if (list.isEmpty()) {
      <div class="card flex flex-col items-center gap-3 px-6 py-14 text-center">
        <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideScrollText class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell transition-opacity"
        tabindex="0"
        role="region"
        aria-labelledby="audit-caption"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table min-w-[52rem]">
          <caption id="audit-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.occurredAt }}</th>
              <th scope="col">{{ text.user }}</th>
              <th scope="col">{{ text.action }}</th>
              <th scope="col">{{ text.record }}</th>
              <th scope="col">{{ text.fields }}</th>
            </tr>
          </thead>
          <tbody>
            @for (entry of list.items(); track entry.id) {
              <tr>
                <td class="font-mono text-xs whitespace-nowrap">{{ time(entry.occurredAt) }}</td>
                <td>
                  <span class="block">{{
                    entry.userName ?? (entry.userId ? text.unknownUser : text.system)
                  }}</span>
                  @if (entry.userRole) {
                    <span class="block text-xs text-muted-foreground">{{
                      role(entry.userRole)
                    }}</span>
                  }
                </td>
                <td>
                  <span class="badge" [class.badge-muted]="isRead(entry)">{{
                    text.actions[entry.action]
                  }}</span>
                  @if (entry.reason) {
                    <span class="mt-1 block text-xs text-muted-foreground">{{
                      reason(entry.reason)
                    }}</span>
                  }
                </td>
                <td>
                  <span class="block">{{ text.entityTypes[entry.entityType] }}</span>
                  @if (entry.entityId; as id) {
                    @if (entry.entityType === 'Student') {
                      <a
                        class="font-mono text-xs text-primary underline-offset-4 hover:underline"
                        [routerLink]="['/students', id]"
                        [attr.aria-label]="interpolate(text.openStudent, { id: shortId(id) })"
                        >{{ shortId(id) }}</a
                      >
                    } @else {
                      <span class="font-mono text-xs text-muted-foreground">{{ shortId(id) }}</span>
                    }
                  } @else {
                    <span class="text-xs text-muted-foreground">{{ text.listQuery }}</span>
                  }
                </td>
                <td class="text-xs">
                  {{ entry.fields.length > 0 ? fieldList(entry.fields) : '—' }}
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
export class AuditLogPage {
  private readonly api = inject(Api);
  private readonly clock = inject(SchoolClock);

  protected readonly text = ui.auditLog;
  protected readonly common = ui.common;
  protected readonly types = ENTITY_TYPES;
  protected readonly interpolate = interpolate;

  protected readonly entityType = signal<AuditEntityType | ''>('');
  protected readonly userId = signal('');
  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly actors = signal<AuditActorDto[]>([]);
  protected readonly rangeInvalid = computed(
    () => this.from() !== '' && this.to() !== '' && this.from() > this.to(),
  );
  protected readonly resultText = computed(() =>
    this.list.loading() || this.list.refreshing()
      ? ''
      : interpolate(this.text.resultCount, { count: this.list.totalCount() }),
  );

  protected readonly list = new PagedList<AuditEntryDto>((page) =>
    this.api.invoke(apiV1AuditLogGet, {
      entityType: this.entityType() || undefined,
      userId: this.userId() || undefined,
      from: this.from() || undefined,
      to: this.to() || undefined,
      page,
      pageSize: AUDIT_PAGE_SIZE,
    }),
  );

  constructor() {
    void this.list.load();
    void this.loadActors();
  }

  protected setType(value: string): void {
    this.entityType.set(value as AuditEntityType | '');
    void this.list.reset();
  }

  protected setUser(value: string): void {
    this.userId.set(value);
    void this.list.reset();
  }

  protected setFrom(value: string): void {
    this.from.set(value);
    this.reloadIfValid();
  }

  protected setTo(value: string): void {
    this.to.set(value);
    this.reloadIfValid();
  }

  /** The entry's time in the school's time zone. */
  protected time(iso: string): string {
    return absoluteTime(iso, this.clock.timeZone());
  }

  protected role(role: string): string {
    return ui.roles[role] ?? role;
  }

  protected reason(code: string): string {
    return this.text.reasons[code] ?? code;
  }

  protected isRead(entry: AuditEntryDto): boolean {
    return entry.action === 'Read' || entry.action === 'List';
  }

  /** Field (or filter) names in the active language where known; unknown names are shown as they are. */
  protected fieldList(fields: string[]): string {
    return fields.map((f) => this.text.fieldNames[f] ?? f).join(', ');
  }

  /** The random tail of a UUIDv7, enough to tell records apart on one screen. */
  protected shortId(id: string): string {
    return id.replace(/-/g, '').slice(-8);
  }

  protected actorLabel(actor: AuditActorDto): string {
    const name = actor.userName ?? this.text.unknownUser;
    return actor.userRole ? `${name} (${this.role(actor.userRole)})` : name;
  }

  private reloadIfValid(): void {
    if (!this.rangeInvalid()) {
      void this.list.reset();
    }
  }

  /** The user filter's options; the list works without them. */
  private async loadActors(): Promise<void> {
    try {
      this.actors.set(await this.api.invoke(apiV1AuditLogActorsGet));
    } catch {
      // Without options only the user filter is unavailable.
    }
  }
}
