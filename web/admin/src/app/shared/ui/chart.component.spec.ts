import { deepMerge, themedOption } from './chart.component';

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
