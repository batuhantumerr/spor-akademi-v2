import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideGraduationCap, LucidePlus, LucideSearch } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1GroupsGet, apiV1SportsGet, apiV1StudentsGet } from '../../api/functions';
import { NamedReferenceDto, StudentListItemDto, StudentStatus } from '../../api/models';
import { formatDate } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { ageOn } from '../../core/students/age';
import { SchoolClock } from '../../core/time/school-clock';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';

/** Typing pause before the search is sent. */
export const SEARCH_DEBOUNCE_MS = 300;

type StatusFilter = StudentStatus | '';
type Sort = 'lastName' | '-createdAt';

/** US-10: search and browse students. The API folds Turkish letters and case for the search. */
@Component({
  selector: 'app-students-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    PaginationComponent,
    LucideGraduationCap,
    LucidePlus,
    LucideSearch,
  ],
  template: `
    <app-page-header [title]="text.title" [description]="text.description">
      <a routerLink="/students/new" class="btn btn-primary">
        <svg lucidePlus></svg>{{ text.add }}
      </a>
    </app-page-header>

    <div class="card mb-4 grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-5" role="search">
      <div class="min-w-0 sm:col-span-2 lg:col-span-1">
        <label class="field-label" for="student-search">{{ text.search }}</label>
        <div class="relative">
          <svg
            lucideSearch
            class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
            aria-hidden="true"
          ></svg>
          <input
            id="student-search"
            type="search"
            class="input pl-9"
            maxlength="100"
            autocomplete="off"
            [placeholder]="text.searchPlaceholder"
            [value]="searchText()"
            (input)="onSearch($any($event.target).value)"
          />
        </div>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="student-status">{{ text.status }}</label>
        <select
          id="student-status"
          class="input"
          (change)="setFilter('status', $any($event.target).value)"
        >
          <option value="Active" [selected]="status() === 'Active'">
            {{ text.statuses['Active'] }}
          </option>
          <option value="Left" [selected]="status() === 'Left'">{{ text.statuses['Left'] }}</option>
          <option value="" [selected]="status() === ''">{{ text.allStatuses }}</option>
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="student-sport">{{ text.sport }}</label>
        <select
          id="student-sport"
          class="input"
          (change)="setFilter('sport', $any($event.target).value)"
        >
          <option value="">{{ text.allSports }}</option>
          @for (sport of sports(); track sport.id) {
            <option [value]="sport.id" [selected]="sportId() === sport.id">{{ sport.name }}</option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="student-group">{{ text.group }}</label>
        <select
          id="student-group"
          class="input"
          (change)="setFilter('group', $any($event.target).value)"
        >
          <option value="">{{ text.allGroups }}</option>
          @for (group of groups(); track group.id) {
            <option [value]="group.id" [selected]="groupId() === group.id">{{ group.name }}</option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="student-sort">{{ text.sort }}</label>
        <select
          id="student-sort"
          class="input"
          (change)="setFilter('sort', $any($event.target).value)"
        >
          <option value="lastName" [selected]="sort() === 'lastName'">
            {{ text.sorts['lastName'] }}
          </option>
          <option value="-createdAt" [selected]="sort() === '-createdAt'">
            {{ text.sorts['-createdAt'] }}
          </option>
        </select>
      </div>
    </div>

    <!-- Announces the result count after a search or filter change. -->
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
          <svg lucideGraduationCap class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">
          {{ filtered() ? text.emptyFiltered : text.empty }}
        </p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell transition-opacity"
        tabindex="0"
        role="region"
        aria-labelledby="students-caption"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table min-w-[40rem]">
          <caption id="students-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.name }}</th>
              <th scope="col">{{ text.birthDate }}</th>
              <th scope="col">{{ text.gender }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col">{{ text.leftAt }}</th>
            </tr>
          </thead>
          <tbody>
            @for (student of list.items(); track student.id) {
              <tr>
                <th scope="row">
                  <a
                    class="text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/students', student.id]"
                    >{{ student.firstName }} {{ student.lastName }}</a
                  >
                </th>
                <td class="whitespace-nowrap">
                  <span class="font-mono text-xs">{{ date(student.birthDate) }}</span>
                  <span class="ml-1 text-xs text-muted-foreground">{{
                    age(student.birthDate)
                  }}</span>
                </td>
                <td>{{ text.genders[student.gender] }}</td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="student.status === 'Active'"
                    [class.badge-muted]="student.status !== 'Active'"
                  >
                    {{ text.statuses[student.status] }}
                  </span>
                </td>
                <td class="font-mono text-xs">
                  {{ student.leftAt ? date(student.leftAt) : '—' }}
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
export class StudentsPage {
  private readonly api = inject(Api);

  protected readonly text = ui.students;
  protected readonly common = ui.common;

  /** What is in the box; `search` follows it after a typing pause. */
  protected readonly searchText = signal('');
  protected readonly search = signal('');
  protected readonly status = signal<StatusFilter>('Active');
  protected readonly sportId = signal('');
  protected readonly groupId = signal('');
  protected readonly sort = signal<Sort>('lastName');
  protected readonly sports = signal<NamedReferenceDto[]>([]);
  protected readonly groups = signal<NamedReferenceDto[]>([]);
  protected readonly filtered = computed(
    () =>
      this.search() !== '' ||
      this.status() !== '' ||
      this.sportId() !== '' ||
      this.groupId() !== '',
  );
  protected readonly resultText = computed(() =>
    this.list.loading() || this.list.refreshing()
      ? ''
      : interpolate(this.text.resultCount, { count: this.list.totalCount() }),
  );

  protected readonly list = new PagedList<StudentListItemDto>((page) =>
    this.api.invoke(apiV1StudentsGet, {
      search: this.search() || undefined,
      status: this.status() || undefined,
      sportId: this.sportId() || undefined,
      groupId: this.groupId() || undefined,
      sort: this.sort(),
      page,
      pageSize: 25,
    }),
  );

  private readonly clock = inject(SchoolClock);
  private debounce: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    void this.list.load();
    void this.loadOptions();
    inject(DestroyRef).onDestroy(() => this.cancelDebounce());
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected age(birthIso: string): string {
    const age = ageOn(birthIso, this.clock.today());
    return age === null ? '' : `(${interpolate(this.text.age, { age })})`;
  }

  protected onSearch(value: string): void {
    this.searchText.set(value);
    this.cancelDebounce();
    this.debounce = setTimeout(() => {
      this.debounce = null;
      const next = value.trim();
      if (next !== this.search()) {
        this.search.set(next);
        void this.list.reset();
      }
    }, SEARCH_DEBOUNCE_MS);
  }

  protected setFilter(filter: 'status' | 'sport' | 'group' | 'sort', value: string): void {
    if (filter === 'status') {
      this.status.set(value as StatusFilter);
    } else if (filter === 'sport') {
      this.sportId.set(value);
    } else if (filter === 'group') {
      this.groupId.set(value);
    } else {
      this.sort.set(value as Sort);
    }
    void this.list.reset();
  }

  private cancelDebounce(): void {
    if (this.debounce) {
      clearTimeout(this.debounce);
      this.debounce = null;
    }
  }

  /** Filter options; the list works without them, so failures only leave the selects short. */
  private async loadOptions(): Promise<void> {
    const [sports, groups] = await Promise.allSettled([
      this.api.invoke(apiV1SportsGet, { includeArchived: false, page: 1, pageSize: 100 }),
      this.api.invoke(apiV1GroupsGet, { status: 'Active', page: 1, pageSize: 100 }),
    ]);
    if (sports.status === 'fulfilled') {
      this.sports.set(sports.value.items.map(({ id, name }) => ({ id, name })));
    }
    if (groups.status === 'fulfilled') {
      this.groups.set(groups.value.items.map(({ id, name }) => ({ id, name })));
    }
  }
}
