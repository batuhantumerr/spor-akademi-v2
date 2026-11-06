import { TestBed } from '@angular/core/testing';

import { ThemeService } from '../../core/theme/theme.service';
import { ChartComponent, deepMerge, themedOption } from './chart.component';

type Option = Record<string, unknown>;

describe('themedOption', () => {
  it('keeps themed tooltip styling when the caller sets a trigger', () => {
    const option = themedOption({ tooltip: { trigger: 'axis' } }) as Option;
    const tooltip = option['tooltip'] as Option;
    expect(tooltip['trigger']).toBe('axis');
    expect(tooltip['textStyle']).toEqual(expect.objectContaining({ fontSize: 12 }));
  });

  it('merges grid overrides with the defaults', () => {
    const grid = (themedOption({ grid: { top: 48 } }) as Option)['grid'] as Option;
    expect(grid).toEqual(expect.objectContaining({ top: 48, left: 8, containLabel: true }));
  });

  it('applies axis defaults to every axis in an array', () => {
    const option = themedOption({
      xAxis: { type: 'category', data: ['a'] },
      yAxis: [{ type: 'value' }, { type: 'value', axisLabel: { formatter: '{value} %' } }],
    }) as Option;
    const x = option['xAxis'] as Option;
    const [left, right] = option['yAxis'] as Option[];
    expect(x['data']).toEqual(['a']);
    expect(x['axisTick']).toEqual({ show: false });
    expect(left['splitLine']).toEqual(expect.objectContaining({ lineStyle: expect.any(Object) }));
    expect(right['axisLabel']).toEqual(
      expect.objectContaining({ formatter: '{value} %', color: expect.any(String) }),
    );
  });

  it('adds no axes to charts without them and enables the ARIA description', () => {
    const option = themedOption({ series: [{ type: 'pie', data: [] }] }) as Option;
    expect(option['xAxis']).toBeUndefined();
    expect(option['aria']).toEqual({ enabled: true });
  });

  it('replaces arrays instead of merging them', () => {
    expect(deepMerge({ a: [1, 2], b: { c: 1 } }, { a: [3] })).toEqual({ a: [3], b: { c: 1 } });
  });
});

describe('ChartComponent', () => {
  let tokens: HTMLStyleElement;

  beforeEach(() => {
    // jsdom has no matchMedia; ThemeService reads the system preference from it.
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () => ({ matches: false, addEventListener: () => undefined }),
    });
    tokens = document.createElement('style');
    tokens.textContent = ':root { --chart-1: #2563eb; } .dark { --chart-1: #60a5fa; }';
    document.head.appendChild(tokens);
    // No canvas in jsdom: keep the component's logic, drop the echarts directive.
    TestBed.overrideComponent(ChartComponent, { set: { imports: [], template: '' } });
  });

  afterEach(() => {
    tokens.remove();
    document.documentElement.classList.remove('dark', 'light');
    try {
      localStorage.removeItem('sa-theme');
    } catch {
      // ignore
    }
  });

  it('re-reads the token colours when the theme flips', () => {
    const theme = TestBed.inject(ThemeService);
    theme.set('light');
    const fixture = TestBed.createComponent(ChartComponent);
    fixture.componentRef.setInput('options', { series: [] });
    const merged = () => (fixture.componentInstance['merged']() as Option)['color'] as string[];

    expect(merged()[0]).toBe('#2563eb');
    theme.set('dark');
    expect(merged()[0]).toBe('#60a5fa');
    theme.set('light');
    expect(merged()[0]).toBe('#2563eb');
  });
});
