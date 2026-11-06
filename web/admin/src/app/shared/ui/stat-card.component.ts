import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { LucideDynamicIcon, LucideIconInput } from '@lucide/angular';

export type StatTone = 'primary' | 'success' | 'warning' | 'danger' | 'info';

const toneClasses: Record<StatTone, { glow: string; icon: string }> = {
  primary: { glow: 'bg-primary', icon: 'text-primary' },
  success: { glow: 'bg-success', icon: 'text-success' },
  warning: { glow: 'bg-warning', icon: 'text-warning' },
  danger: { glow: 'bg-destructive', icon: 'text-destructive' },
  info: { glow: 'bg-info', icon: 'text-info' },
};

/** KPI tile: label, big mono value, optional hint, tinted glow. */
@Component({
  selector: 'app-stat-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideDynamicIcon],
  template: `
    <div
      class="relative h-full overflow-hidden rounded-3xl border border-border bg-card p-4 shadow-sm transition-shadow hover:shadow-md"
    >
      <div
        class="pointer-events-none absolute -top-16 -right-16 size-44 rounded-full opacity-[0.08] blur-3xl"
        [class]="tone().glow"
      ></div>
      <div class="flex items-start justify-between gap-3">
        <p class="text-[11px] font-bold tracking-wider text-muted-foreground uppercase">
          {{ label() }}
        </p>
        @if (icon()) {
          <div
            class="grid size-8 place-items-center rounded-xl border border-border bg-muted/70"
            [class]="tone().icon"
          >
            <svg [lucideIcon]="icon()!" class="size-4"></svg>
          </div>
        }
      </div>
      <p class="mt-3 font-mono text-2xl font-extrabold tracking-tight text-foreground">
        {{ value() }}
      </p>
      @if (hint()) {
        <p class="mt-1 text-xs text-muted-foreground">{{ hint() }}</p>
      }
    </div>
  `,
})
export class StatCardComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly hint = input<string>();
  readonly icon = input<LucideIconInput>();
  readonly color = input<StatTone>('primary');

  protected readonly tone = computed(() => toneClasses[this.color()]);
}
