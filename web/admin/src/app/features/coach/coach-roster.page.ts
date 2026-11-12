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
import { LucideArrowLeft, LucidePhone } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1GroupsIdRosterGet, apiV1MeGroupsGet } from '../../api/functions';
import { RosterEntryDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatPhone } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';

/**
 * US-12: a coach's roster for one group, phone first (NFR-USA-01). Only what the coach needs in
 * the field: names, birth year and a one-tap call to the emergency contact.
 */
@Component({
  selector: 'app-coach-roster-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LucideArrowLeft, LucidePhone],
  template: `
    <div class="mx-auto w-full max-w-xl">
      <a routerLink="/coach/groups" class="btn btn-ghost mb-2 -ml-2 h-11">
        <svg lucideArrowLeft></svg>{{ text.back }}
      </a>
      <h1 class="mb-4 text-xl font-bold tracking-tight">
        {{ groupName() ?? text.title }}
        @if (groupName()) {
          <span class="block text-sm font-normal text-muted-foreground">{{ text.title }}</span>
        }
      </h1>

      @if (error(); as message) {
        <div role="alert" class="card flex flex-col gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary h-12" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (roster() === null) {
        <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (roster()!.length === 0) {
        <p class="card p-6 text-base text-muted-foreground">{{ text.empty }}</p>
      } @else {
        <ul class="space-y-2">
          @for (entry of roster(); track $index) {
            <li class="card flex items-center justify-between gap-3 p-3">
              <div class="min-w-0">
                <p class="text-base font-semibold break-words">
                  {{ entry.firstName }} {{ entry.lastName }}
                </p>
                <p class="text-sm text-muted-foreground">{{ birthYear(entry) }}</p>
              </div>
              <a
                class="btn btn-secondary h-12 shrink-0 px-4"
                [href]="'tel:' + entry.emergencyContactPhone"
                [attr.aria-label]="callLabel(entry)"
              >
                <svg lucidePhone></svg>
                <span class="font-mono text-sm">{{ phone(entry.emergencyContactPhone) }}</span>
              </a>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class CoachRosterPage {
  private readonly api = inject(Api);

  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  protected readonly text = ui.roster;
  protected readonly common = ui.common;
  protected readonly roster = signal<RosterEntryDto[] | null>(null);
  protected readonly groupName = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  private sequence = 0;

  constructor() {
    effect(() => {
      this.id();
      untracked(() => {
        this.roster.set(null);
        this.groupName.set(null);
        void this.load();
      });
    });
  }

  protected birthYear(entry: RosterEntryDto): string {
    return interpolate(this.text.birthYear, { year: entry.birthYear });
  }

  protected phone(value: string): string {
    return formatPhone(value);
  }

  protected callLabel(entry: RosterEntryDto): string {
    return `${entry.firstName} ${entry.lastName} – ${interpolate(this.text.call, {
      phone: formatPhone(entry.emergencyContactPhone),
    })}`;
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    const id = this.id();
    this.error.set(null);
    try {
      const [roster, groups] = await Promise.all([
        this.api.invoke(apiV1GroupsIdRosterGet, { id }),
        // The name comes from the coach's own groups; a missing name is not an error.
        this.api.invoke(apiV1MeGroupsGet).catch(() => []),
      ]);
      if (request !== this.sequence) {
        return;
      }
      this.roster.set(roster);
      this.groupName.set(groups.find((g) => g.id === id)?.name ?? null);
    } catch (err) {
      if (request !== this.sequence) {
        return;
      }
      const problem = toProblem(err);
      this.error.set(
        problem.status === 404 || problem.status === 403
          ? this.text.notFound
          : problemMessage(problem),
      );
    }
  }
}
