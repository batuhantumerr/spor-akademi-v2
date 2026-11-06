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
  linkedSignal,
  output,
} from '@angular/core';
import { LucidePlus, LucideTrash2, LucideTriangleAlert } from '@lucide/angular';

import { DayOfWeek, ScheduleSlotDto, WarningDto } from '../../api/models';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import {
  MAX_SLOTS,
  SlotDraft,
  WEEK,
  hasIssues,
  slotLabel,
  sortSlots,
  validateSlots,
} from '../../core/schedule/schedule';

/** A row of the editor; the id keeps per-row state stable when rows are removed. */
interface Row extends SlotDraft {
  id: number;
}

let nextRowId = 1;

function toRows(slots: readonly SlotDraft[]): Row[] {
  return sortSlots(slots).map(({ day, start, end }) => ({ id: nextRowId++, day, start, end }));
}

function sameSlot(a: SlotDraft, b: SlotDraft): boolean {
  return a.day === b.day && a.start === b.start && a.end === b.end;
}

/**
 * Weekly schedule editor (US-09): one row per slot with day, start and end (24h HH:mm). Checks
 * format, order and same-day overlaps before emitting `save`; shows the API's warnings as a
 * non-blocking notice and its error as a focused alert naming the slot when the API points at one.
 */
@Component({
  selector: 'app-schedule-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucidePlus, LucideTrash2, LucideTriangleAlert],
  template: `
    <form (submit)="$event.preventDefault(); submit()" novalidate>
      <p id="schedule-hint" class="text-sm text-muted-foreground">{{ text.scheduleHint }}</p>

      @if (error()) {
        <p
          id="schedule-error"
          role="alert"
          tabindex="-1"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ alertText() }}
        </p>
      }
      <div role="status">
        @if (warningMessages().length > 0) {
          <div
            class="mt-3 flex items-start gap-2 rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm text-warning"
          >
            <svg lucideTriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true"></svg>
            <div>
              <p class="font-semibold">{{ text.warningsTitle }}</p>
              <ul class="mt-1 list-disc space-y-1 pl-4">
                @for (message of warningMessages(); track $index) {
                  <li>{{ message }}</li>
                }
              </ul>
            </div>
          </div>
        }
      </div>

      @if (rows().length === 0) {
        <p class="mt-4 text-sm text-muted-foreground">{{ text.noSlots }}</p>
      }
      <ol class="mt-4 space-y-3">
        @for (row of rows(); track row.id; let i = $index) {
          <li>
            <fieldset
              class="grid grid-cols-2 items-start gap-2 rounded-lg border border-border p-3 sm:grid-cols-[1fr_1fr_1fr_auto]"
            >
              <legend class="sr-only">{{ label(i) }}</legend>
              <div class="col-span-2 sm:col-span-1">
                <label class="field-label" [for]="'slot-day-' + i">{{ text.day }}</label>
                <select
                  class="input pointer-coarse:h-11"
                  [id]="'slot-day-' + i"
                  [disabled]="readonly()"
                  (change)="update(i, 'day', $any($event.target).value)"
                >
                  @for (day of week; track day) {
                    <option [value]="day" [selected]="day === row.day">{{ dayName(day) }}</option>
                  }
                </select>
              </div>
              @for (field of timeFields; track field) {
                <div>
                  <label class="field-label" [for]="'slot-' + field + '-' + i">{{
                    text[field]
                  }}</label>
                  <input
                    class="input font-mono pointer-coarse:h-11"
                    [id]="'slot-' + field + '-' + i"
                    inputmode="numeric"
                    maxlength="5"
                    [placeholder]="field === 'start' ? '17:00' : '18:30'"
                    autocomplete="off"
                    [value]="row[field]"
                    [disabled]="readonly()"
                    [attr.aria-invalid]="!!issue(i, field)"
                    [attr.aria-describedby]="
                      issue(i, field) ? 'slot-' + field + '-' + i + '-error' : 'schedule-hint'
                    "
                    (input)="update(i, field, $any($event.target).value)"
                    (blur)="touch(row.id)"
                  />
                  @if (issue(i, field); as m) {
                    <p [id]="'slot-' + field + '-' + i + '-error'" class="field-error">{{ m }}</p>
                  }
                </div>
              }
              @if (!readonly()) {
                <button
                  type="button"
                  class="btn btn-ghost btn-icon col-span-2 justify-self-end hover:!bg-destructive/10 hover:!text-destructive sm:col-span-1 sm:mt-6"
                  [attr.aria-label]="text.removeSlot + ': ' + label(i)"
                  [attr.title]="text.removeSlot"
                  (click)="remove(row.id)"
                >
                  <svg lucideTrash2></svg>
                </button>
              }
            </fieldset>
          </li>
        }
      </ol>

      @if (!readonly()) {
        <div class="mt-4 flex flex-wrap items-center justify-between gap-2">
          <button
            id="slot-add"
            type="button"
            class="btn btn-secondary"
            [disabled]="rows().length >= maxSlots"
            [attr.aria-describedby]="rows().length >= maxSlots ? 'slot-limit' : null"
            (click)="add()"
          >
            <svg lucidePlus></svg>{{ text.addSlot }}
          </button>
          @if (rows().length >= maxSlots) {
            <p id="slot-limit" class="text-xs text-muted-foreground">{{ text.tooManySlots }}</p>
          }
          <button type="submit" class="btn btn-primary" [disabled]="saving()">
            {{ text.saveSchedule }}
          </button>
        </div>
      }
    </form>
  `,
})
export class ScheduleEditorComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly slots = input.required<ScheduleSlotDto[]>();
  readonly warnings = input<WarningDto[]>([]);
  readonly error = input<string | null>(null);
  /** The slot the API's error is about (from `slots[i]` in the problem), if any. */
  readonly errorSlot = input<SlotDraft | null>(null);
  readonly saving = input(false);
  readonly readonly = input(false);
  readonly save = output<SlotDraft[]>();

  protected readonly text = ui.groupDetail;
  protected readonly week = WEEK;
  protected readonly maxSlots = MAX_SLOTS;
  protected readonly timeFields = ['start', 'end'] as const;

  /** Editable copy; reset only when the parent passes new slots (load, successful save, discard). */
  protected readonly rows = linkedSignal<Row[]>(() => toRows(this.slots()));
  private readonly touched = linkedSignal<Set<number>>(() => {
    this.slots();
    return new Set<number>();
  });
  private readonly submitted = linkedSignal(() => {
    this.slots();
    return false;
  });
  private readonly issues = computed(() => validateSlots(this.rows()));
  /** Row index the API error points at. */
  private readonly errorRow = computed(() => {
    const slot = this.errorSlot();
    return slot ? this.rows().findIndex((r) => sameSlot(r, slot)) : -1;
  });

  protected readonly alertText = computed(() => {
    const row = this.errorRow();
    return row >= 0 ? `${this.error()} (${slotLabel(row)})` : this.error();
  });

  protected readonly warningMessages = computed(() =>
    this.warnings().map((w) =>
      interpolate(
        errorMessages[w.code] ?? errorMessages['Unexpected.Error'],
        w.args as Record<string, unknown>,
      ),
    ),
  );

  constructor() {
    // Move focus to a new API error so screen-reader and keyboard users land on it.
    effect(() => {
      if (this.error()) {
        afterNextRender(
          () => this.host.nativeElement.querySelector<HTMLElement>('#schedule-error')?.focus(),
          { injector: this.injector },
        );
      }
    });
  }

  protected label(index: number): string {
    return slotLabel(index);
  }

  protected dayName(day: DayOfWeek): string {
    return ui.days[day].long;
  }

  /**
   * A row's problem, shown after submit, once the row was used, or (for an overlap) once the
   * row it collides with was used; the API's slot error shows on the row it names.
   */
  protected issue(index: number, field: 'start' | 'end'): string | undefined {
    const issue = this.issues()[index];
    const row = this.rows()[index];
    const touched = (k: number) => this.touched().has(this.rows()[k]?.id);
    const shown =
      this.submitted() ||
      touched(index) ||
      (field === 'end' && (issue?.overlapsWith ?? []).some(touched));
    const client = shown ? issue?.[field] : undefined;
    if (client) {
      return client;
    }
    return field === 'end' && row && index === this.errorRow()
      ? (this.error() ?? undefined)
      : undefined;
  }

  protected update(index: number, field: keyof SlotDraft, value: string): void {
    const id = this.rows()[index]?.id;
    this.rows.update((list) =>
      list.map((row, i) => (i === index ? { ...row, [field]: value } : row)),
    );
    // A new day can create or resolve an overlap right away.
    if (field === 'day' && id !== undefined) {
      this.touch(id);
    }
  }

  protected touch(id: number): void {
    if (!this.touched().has(id)) {
      this.touched.update((set) => new Set(set).add(id));
    }
  }

  protected add(): void {
    const list = this.rows();
    if (list.length >= MAX_SLOTS) {
      return;
    }
    const last = list.at(-1);
    const day = last ? WEEK[(WEEK.indexOf(last.day) + 1) % WEEK.length] : 'Monday';
    this.rows.set([
      ...list,
      { id: nextRowId++, day, start: last?.start ?? '', end: last?.end ?? '' },
    ]);
    const index = list.length;
    afterNextRender(() => this.element(`slot-day-${index}`)?.focus(), { injector: this.injector });
  }

  protected remove(id: number): void {
    const index = this.rows().findIndex((r) => r.id === id);
    this.rows.update((list) => list.filter((r) => r.id !== id));
    // Other rows keep their touched state (it is keyed by row id, not position).
    this.touched.update((set) => {
      const next = new Set(set);
      next.delete(id);
      return next;
    });
    const next = Math.min(index, this.rows().length - 1);
    afterNextRender(
      () => (next >= 0 ? this.element(`slot-day-${next}`) : this.element('slot-add'))?.focus(),
      { injector: this.injector },
    );
  }

  protected submit(): void {
    this.submitted.set(true);
    const issues = this.issues();
    if (hasIssues(issues)) {
      const first = issues.findIndex((i) => i.start || i.end);
      const field = issues[first].start ? 'start' : 'end';
      this.element(`slot-${field}-${first}`)?.focus();
      return;
    }
    this.save.emit(sortSlots(this.rows()).map(({ day, start, end }) => ({ day, start, end })));
  }

  private element(id: string): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`);
  }
}
