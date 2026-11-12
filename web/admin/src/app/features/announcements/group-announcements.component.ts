import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { LucideMegaphone } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1GroupsIdAnnouncementsGet, apiV1GroupsIdAnnouncementsPost } from '../../api/functions';
import { AnnouncementDto, GroupDto } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { PagedList } from '../../shared/data/paged-list';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { AnnouncementItemComponent } from './announcement-item.component';

/** Same limits as the API (Announcement.TitleMaxLength / BodyMaxLength). */
export const TITLE_MAX = 150;
export const BODY_MAX = 4000;

type Field = 'title' | 'body';

/** US-15: the school admin publishes plain-text announcements to a group and sees the list. */
@Component({
  selector: 'app-group-announcements',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnnouncementItemComponent, PaginationComponent, LucideMegaphone],
  template: `
    <section class="card p-4" aria-labelledby="announcements-title">
      <h2 id="announcements-title" class="mb-3 text-base font-semibold">{{ text.groupTitle }}</h2>

      @if (group().status === 'Active') {
        <form class="space-y-3" (submit)="$event.preventDefault(); publish()" novalidate>
          @if (error()) {
            <p
              id="announcement-error"
              role="alert"
              tabindex="-1"
              class="rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              {{ error() }}
            </p>
          }
          <div>
            <label class="field-label" for="announcement-title">{{ text.titleLabel }}</label>
            <input
              id="announcement-title"
              class="input"
              autocomplete="off"
              [attr.maxlength]="titleMax"
              [value]="title()"
              (input)="title.set($any($event.target).value); clear('title')"
              [attr.aria-invalid]="!!message('title')"
              [attr.aria-describedby]="
                'announcement-title-count' + (message('title') ? ' announcement-title-error' : '')
              "
            />
            <p
              id="announcement-title-count"
              class="mt-1 text-right font-mono text-xs text-muted-foreground"
            >
              {{ titleCount() }}
            </p>
            @if (message('title'); as m) {
              <p id="announcement-title-error" class="field-error">{{ m }}</p>
            }
          </div>
          <div>
            <label class="field-label" for="announcement-body">{{ text.bodyLabel }}</label>
            <textarea
              id="announcement-body"
              class="input h-auto min-h-32 py-2"
              rows="6"
              [attr.maxlength]="bodyMax"
              [value]="body()"
              (input)="body.set($any($event.target).value); clear('body')"
              [attr.aria-invalid]="!!message('body')"
              [attr.aria-describedby]="
                'announcement-body-hint announcement-body-count' +
                (message('body') ? ' announcement-body-error' : '')
              "
            ></textarea>
            <div class="mt-1 flex justify-between gap-3 text-xs text-muted-foreground">
              <p id="announcement-body-hint">{{ text.bodyHint }}</p>
              <p id="announcement-body-count" class="shrink-0 font-mono">{{ bodyCount() }}</p>
            </div>
            @if (message('body'); as m) {
              <p id="announcement-body-error" class="field-error">{{ m }}</p>
            }
          </div>
          <div class="flex justify-end">
            <!-- Stays focusable while publishing (aria-disabled, not disabled) so focus is not lost. -->
            <button
              type="submit"
              class="btn btn-primary"
              [class.opacity-60]="publishing()"
              [attr.aria-disabled]="publishing()"
              [attr.aria-busy]="publishing()"
            >
              <svg lucideMegaphone></svg>{{ publishing() ? text.publishing : text.publish }}
            </button>
          </div>
        </form>
      } @else {
        <p class="text-sm text-muted-foreground">{{ text.archivedGroup }}</p>
      }

      <!-- Announces a publish and list refreshes without moving focus. -->
      <p class="sr-only" aria-live="polite" data-testid="announcements-live">
        {{ announcement() }}
      </p>

      <div class="mt-4 border-t border-border/60 pt-3">
        @if (list.error(); as message) {
          <div
            role="alert"
            class="flex items-center justify-between gap-3 text-sm text-destructive"
          >
            <span>{{ message }}</span>
            <button type="button" class="btn btn-secondary btn-sm" (click)="list.load()">
              {{ common.retry }}
            </button>
          </div>
        } @else if (list.loading()) {
          <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
        } @else if (list.isEmpty()) {
          <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
        } @else {
          <ul
            class="divide-y divide-border/60"
            [attr.aria-busy]="list.refreshing()"
            [class.opacity-60]="list.refreshing()"
          >
            @for (a of list.items(); track a.id) {
              <li class="py-3"><app-announcement-item [announcement]="a" /></li>
            }
          </ul>
          <app-pagination
            [page]="list.page()"
            [totalPages]="list.totalPages()"
            [totalCount]="list.totalCount()"
            [disabled]="list.refreshing()"
            (pageChange)="list.goTo($event)"
          />
        }
      </div>
    </section>
  `,
})
export class GroupAnnouncementsComponent {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly group = input.required<GroupDto>();

  protected readonly text = ui.announcements;
  protected readonly common = ui.common;
  protected readonly titleMax = TITLE_MAX;
  protected readonly bodyMax = BODY_MAX;
  protected readonly title = signal('');
  protected readonly body = signal('');
  protected readonly publishing = signal(false);
  protected readonly submitted = signal(false);
  protected readonly error = signal<string | null>(null);
  /** Polite live region text: only the list reload that follows a successful publish. */
  protected readonly announcement = signal('');
  private readonly serverErrors = signal<Partial<Record<Field, string>>>({});
  protected readonly titleCount = computed(() =>
    interpolate(this.text.counter, { count: this.title().length, max: TITLE_MAX }),
  );
  protected readonly bodyCount = computed(() =>
    interpolate(this.text.counter, { count: this.body().length, max: BODY_MAX }),
  );
  protected readonly list = new PagedList<AnnouncementDto>((page) =>
    this.api.invoke(apiV1GroupsIdAnnouncementsGet, { id: this.group().id, page, pageSize: 10 }),
  );
  private readonly groupId = computed(() => this.group().id);

  constructor() {
    effect(() => {
      this.groupId();
      untracked(() => void this.list.reset());
    });
    // Another admin published to this group, or a reconnect: reload silently, keeping the page (P3-08).
    // The admin's own publish reloads the list itself.
    inject(RealtimeService)
      .announcementsChanged(() => this.group().id)
      .pipe(takeUntilDestroyed())
      .subscribe(() => {
        if (!this.publishing()) {
          void this.list.load();
        }
      });
  }

  protected message(field: Field): string | null {
    const server = this.serverErrors()[field];
    if (server) {
      return server;
    }
    if (!this.submitted()) {
      return null;
    }
    const value = (field === 'title' ? this.title() : this.body()).trim();
    const max = field === 'title' ? TITLE_MAX : BODY_MAX;
    if (value === '') {
      return errorMessages['Field.Required'];
    }
    return value.length > max ? errorMessages['Field.TooLong'] : null;
  }

  protected clear(field: Field): void {
    if (this.serverErrors()[field]) {
      this.serverErrors.update((all) => ({ ...all, [field]: undefined }));
    }
  }

  protected async publish(): Promise<void> {
    if (this.publishing()) {
      return;
    }
    this.submitted.set(true);
    const invalid = (['title', 'body'] as const).find((f) => this.message(f));
    if (invalid) {
      this.focus(`announcement-${invalid}`);
      return;
    }
    this.publishing.set(true);
    this.error.set(null);
    this.serverErrors.set({});
    try {
      await this.api.invoke(apiV1GroupsIdAnnouncementsPost, {
        id: this.group().id,
        body: { title: this.title().trim(), body: this.body().trim() },
      });
      this.title.set('');
      this.body.set('');
      this.submitted.set(false);
      this.toasts.success(this.text.published);
      // Clear first so a repeat of the same text is announced again.
      this.announcement.set('');
      await this.list.reset();
      // The toast already says "published"; a failed reload shows its own alert, so stay silent then.
      if (!this.list.error()) {
        this.announcement.set(
          interpolate(this.text.listUpdated, { count: this.list.totalCount() }),
        );
      }
    } catch (err) {
      const problem = toProblem(err);
      const fields = fieldErrors(problem);
      this.serverErrors.set({ title: fields['title'], body: fields['body'] });
      if (fields['title'] || fields['body']) {
        this.focus(fields['title'] ? 'announcement-title' : 'announcement-body');
      } else {
        this.error.set(problemMessage(problem));
        this.focus('announcement-error');
      }
    } finally {
      this.publishing.set(false);
    }
  }

  private focus(id: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`#${id}`)?.focus(), {
      injector: this.injector,
    });
  }
}
