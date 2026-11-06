import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { NgxEchartsDirective, provideEchartsCore } from 'ngx-echarts';
import type { EChartsCoreOption } from 'echarts/core';
import * as echarts from 'echarts/core';
import { BarChart, HeatmapChart, LineChart, PieChart } from 'echarts/charts';
import {
  GridComponent,
  LegendComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components';
import { CanvasRenderer } from 'echarts/renderers';

import { ThemeService } from '../../core/theme/theme.service';

// Only the chart types the app uses are bundled (tree-shaken ECharts core).
echarts.use([
  BarChart,
  LineChart,
  PieChart,
  HeatmapChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  VisualMapComponent,
  CanvasRenderer,
]);

/** Reads a CSS color token so charts follow the light/dark theme (docs/ui-tema.md). */
export function themeColor(name: string): string {
  return (
    getComputedStyle(document.documentElement).getPropertyValue(`--${name}`).trim() || '#2563eb'
  );
}

/**
 * Themed ECharts wrapper. Callers pass series and axes; this adds the shared look: transparent
 * background, Inter font, token colors, dashed grid lines, themed tooltip.
 */
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
    const text = themeColor('muted-foreground');
    const border = themeColor('border');
    return {
      backgroundColor: 'transparent',
      color: ['chart-1', 'chart-2', 'chart-3', 'chart-4', 'chart-5'].map(themeColor),
      textStyle: { fontFamily: 'Inter Variable, Inter, system-ui, sans-serif', color: text },
      grid: { left: 8, right: 16, top: 24, bottom: 8, containLabel: true },
      tooltip: {
        backgroundColor: themeColor('popover'),
        borderColor: border,
        textStyle: { color: themeColor('popover-foreground'), fontSize: 12 },
      },
      xAxis: {
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { color: text },
        splitLine: { show: false },
      },
      yAxis: {
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { color: text },
        splitLine: { lineStyle: { color: border, type: 'dashed' } },
      },
      ...this.options(),
    };
  });
}
