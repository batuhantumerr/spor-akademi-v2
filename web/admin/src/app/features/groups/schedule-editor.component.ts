import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
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

/**
 * Weekly schedule editor (US-09): one row per slot with day, start and end (24h HH:mm). Checks
 * format, order and same-day overlaps before emitting `save`; shows the API's warnings as a
 * non-blocking notice and its error as an alert.
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
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
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

      @if (drafts().length === 0) {
        <p class="mt-4 text-sm text-muted-foreground">{{ text.noSlots }}</p>
      }
      <ol class="mt-4 space-y-3">
        @for (slot of drafts(); track $index; let i = $index) {
          <li>
            <fieldset
              class="grid grid-cols-2 items-start gap-2 rounded-lg border border-border p-3 sm:grid-cols-[1fr_1fr_1fr_auto]"
            >
              <legend class="sr-only">{{ label(i) }}</legend>
              <div class="col-span-2 sm:col-span-1">
                <label class="field-label" [for]="'slot-day-' + i">{{ text.day }}</label>
                <select
                  class="input"
                  [id]="'slot-day-' + i"
                  [value]="slot.day"
                  [disabled]="readonly()"
                  (change)="update(i, 'day', $any($event.target).value)"
                >
                  @for (day of week; track day) {
                    <option [value]="day" [selected]="day === slot.day">{{ dayName(day) }}</option>
                  }
                </select>
              </div>
              <div>
                <label class="field-label" [for]="'slot-start-' + i">{{ text.start }}</label>
                <input
                  class="input font-mono"
                  [id]="'slot-start-' + i"
                  inputmode="numeric"
                  maxlength="5"
                  placeholder="17:00"
                  autocomplete="off"
                  [value]="slot.start"
                  [disabled]="readonly()"
                  [attr.aria-invalid]="!!issue(i, 'start')"
                  [attr.aria-describedby]="
                    issue(i, 'start') ? 'slot-start-' + i + '-error' : 'schedule-hint'
                  "
                  (input)="update(i, 'start', $any($event.target).value)"
                  (blur)="touch(i, 'start')"
                />
                @if (issue(i, 'start'); as m) {
                  <p [id]="'slot-start-' + i + '-error'" class="field-error">{{ m }}</p>
                }
              </div>
              <div>
                <label class="field-label" [for]="'slot-end-' + i">{{ text.end }}</label>
                <input
                  class="input font-mono"
                  [id]="'slot-end-' + i"
                  inputmode="numeric"
                  maxlength="5"
                  placeholder="18:30"
                  autocomplete="off"
                  [value]="slot.end"
                  [disabled]="readonly()"
                  [attr.aria-invalid]="!!issue(i, 'end')"
                  [attr.aria-describedby]="
                    issue(i, 'end') ? 'slot-end-' + i + '-error' : 'schedule-hint'
                  "
                  (input)="update(i, 'end', $any($event.target).value)"
                  (blur)="touch(i, 'end')"
                />
                @if (issue(i, 'end'); as m) {
                  <p [id]="'slot-end-' + i + '-error'" class="field-error">{{ m }}</p>
                }
              </div>
              @if (!readonly()) {
                <button
                  type="button"
                  class="btn btn-ghost btn-icon col-span-2 justify-self-end hover:!bg-destructive/10 hover:!text-destructive sm:col-span-1 sm:mt-6"
                  [attr.aria-label]="text.removeSlot + ': ' + label(i)"
                  [attr.title]="text.removeSlot"
                  (click)="remove(i)"
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
            #addButton
            type="button"
            class="btn btn-secondary"
            [disabled]="drafts().length >= maxSlots"
            [attr.aria-describedby]="drafts().length >= maxSlots ? 'slot-limit' : null"
            (click)="add()"
          >
            <svg lucidePlus></svg>{{ text.addSlot }}
          </button>
          @if (drafts().length >= maxSlots) {
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
  readonly saving = input(false);
  readonly readonly = input(false);
  readonly save = output<SlotDraft[]>();

  protected readonly text = ui.groupDetail;
  protected readonly week = WEEK;
  protected readonly maxSlots = MAX_SLOTS;

  /** Editable copy; reset whenever the parent passes new slots (load, save, conflict reload). */
  protected readonly drafts = linkedSignal<SlotDraft[]>(() =>
    sortSlots(this.slots()).map(({ day, start, end }) => ({ day, start, end })),
  );
  private readonly touched = linkedSignal<Set<string>>(() => {
    this.slots();
    return new Set<string>();
  });
  private readonly submitted = linkedSignal(() => {
    this.slots();
    return false;
  });
  private readonly issues = computed(() => validateSlots(this.drafts()));

  protected readonly warningMessages = computed(() =>
    this.warnings().map((w) =>
      interpolate(
        errorMessages[w.code] ?? errorMessages['Unexpected.Error'],
        w.args as Record<string, unknown>,
      ),
    ),
  );

  protected label(index: number): string {
    return slotLabel(index);
  }

  protected dayName(day: DayOfWeek): string {
    return ui.days[day].long;
  }

  /** A row's problem, shown once that field was left or the form was submitted. */
  protected issue(index: number, field: 'start' | 'end'): string | undefined {
    const shown =
      this.submitted() ||
      this.touched().has(`${index}-${field}`) ||
      // An overlap or order problem is reported on "end" once either time was touched.
      (field === 'end' &&
        this.touched().has(`${index}-start`) &&
        isTimeFilled(this.drafts()[index]));
    return shown ? this.issues()[index]?.[field] : undefined;
  }

  protected update(index: number, field: keyof SlotDraft, value: string): void {
    this.drafts.update((list) =>
      list.map((slot, i) => (i === index ? { ...slot, [field]: value } : slot)),
    );
  }

  protected touch(index: number, field: 'start' | 'end'): void {
    this.touched.update((set) => new Set(set).add(`${index}-${field}`));
  }

  protected add(): void {
    const list = this.drafts();
    if (list.length >= MAX_SLOTS) {
      return;
    }
    const last = list.at(-1);
    const day = last ? WEEK[(WEEK.indexOf(last.day) + 1) % WEEK.length] : 'Monday';
    this.drafts.set([...list, { day, start: last?.start ?? '', end: last?.end ?? '' }]);
    const index = list.length;
    afterNextRender(() => this.element(`slot-day-${index}`)?.focus(), { injector: this.injector });
  }

  protected remove(index: number): void {
    this.drafts.update((list) => list.filter((_, i) => i !== index));
    // Index-based keys shift; start the "touched" bookkeeping over for the remaining rows.
    this.touched.set(new Set());
    const next = Math.min(index, this.drafts().length - 1);
    afterNextRender(
      () =>
        (next >= 0
          ? this.element(`slot-day-${next}`)
          : this.host.nativeElement.querySelector<HTMLElement>('button[type="button"]')
        )?.focus(),
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
    this.save.emit(sortSlots(this.drafts()));
  }

  private element(id: string): HTMLElement | null {
    return this.host.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`);
  }
}

function isTimeFilled(slot: SlotDraft | undefined): boolean {
  return !!slot && slot.start !== '' && slot.end !== '';
}
