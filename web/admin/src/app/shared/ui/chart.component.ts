import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgxEchartsDirective, provideEchartsCore } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';
import * as echarts from 'echarts/core';
import { BarChart, LineChart, PieChart } from 'echarts/charts';
import {
  AriaComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';

import { ThemeService } from '../../core/theme/theme.service';

// Only the chart types and components the app uses are bundled (tree-shaken ECharts core);
// register more here when a chart needs them (e.g. HeatmapChart + VisualMapComponent).
// AriaComponent adds a generated text description for screen readers (NFR-USA).
echarts.use([
  BarChart,
  LineChart,
  PieChart,
  AriaComponent,
  GridComponent,
  LegendComponent,
  TooltipComponent,
  CanvasRenderer,
]);

/** Reads a CSS color token so charts follow the light/dark theme (docs/ui-tema.md). */
export function themeColor(name: string): string {
  return (
    getComputedStyle(document.documentElement).getPropertyValue(`--${name}`).trim() || '#2563eb'
  );
}

type PlainObject = Record<string, unknown>;

function isPlainObject(value: unknown): value is PlainObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Recursively merges plain objects; arrays and primitives from `override` replace `base`. */
export function deepMerge<T extends PlainObject>(base: T, override: PlainObject | undefined): T {
  if (!override) {
    return base;
  }
  const result: PlainObject = { ...base };
  for (const [key, value] of Object.entries(override)) {
    const current = result[key];
    result[key] =
      isPlainObject(current) && isPlainObject(value) ? deepMerge(current, value) : value;
  }
  return result as T;
}

/** Applies themed defaults to one component option or to each entry of an array of them. */
function withDefaults(defaults: PlainObject, value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((entry) => (isPlainObject(entry) ? deepMerge(defaults, entry) : entry));
  }
  return isPlainObject(value) ? deepMerge(defaults, value) : value;
}

/**
 * Builds the final chart option: the caller's option layered over the shared look (transparent
 * background, Inter font, token colors, dashed grid lines, themed tooltip, ARIA description).
 * Nested objects are merged, so a caller can set `tooltip.trigger` or `xAxis.data` without losing
 * the theme; arrays of axes get the defaults per axis.
 */
export function themedOption(caller: EChartsCoreOption): EChartsCoreOption {
  const text = themeColor('muted-foreground');
  const border = themeColor('border');
  const axis = {
    axisLine: { show: false },
    axisTick: { show: false },
    axisLabel: { color: text },
  };
  const { xAxis, yAxis, ...rest } = caller as PlainObject;

  const option: PlainObject = deepMerge(
    {
      backgroundColor: 'transparent',
      color: ['chart-1', 'chart-2', 'chart-3', 'chart-4', 'chart-5'].map(themeColor),
      textStyle: { fontFamily: 'Inter Variable, Inter, system-ui, sans-serif', color: text },
      aria: { enabled: true },
      grid: { left: 8, right: 16, top: 24, bottom: 8, containLabel: true },
      tooltip: {
        backgroundColor: themeColor('popover'),
        borderColor: border,
        textStyle: { color: themeColor('popover-foreground'), fontSize: 12 },
      },
    },
    rest,
  );
  // Cartesian charts get both axes (a missing one falls back to the themed default, as ECharts
  // needs both); charts without axes, such as pie, get none.
  if (xAxis !== undefined || yAxis !== undefined) {
    option['xAxis'] = withDefaults({ ...axis, splitLine: { show: false } }, xAxis ?? {});
    option['yAxis'] = withDefaults(
      { ...axis, splitLine: { lineStyle: { color: border, type: 'dashed' } } },
      yAxis ?? {},
    );
  }
  return option as EChartsCoreOption;
}

/** Themed ECharts wrapper. Callers pass series and axes; see {@link themedOption}. */
@Component({
  selector: 'app-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgxEchartsDirective],
  providers: [provideEchartsCore({ echarts })],
  template: `<div
    echarts
    [options]="merged()"
    [autoResize]="true"
    class="w-full"
    [style.height.px]="height()"
  ></div>`,
})
export class ChartComponent {
  private readonly theme = inject(ThemeService);

  readonly options = input.required<EChartsCoreOption>();
  readonly height = input(280);

  protected readonly merged = computed<EChartsCoreOption>(() => {
    // Re-evaluated when the theme flips, so token colors are re-read.
    this.theme.isDark();
    return themedOption(this.options());
  });
}
