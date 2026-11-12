import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import {
  AttendanceReportDto,
  AttendanceRowDto,
  OccupancyReportDto,
  RevenuePeriodDto,
  RevenueReportDto,
} from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { money } from '../../core/money/money';
import { ThemeService } from '../../core/theme/theme.service';
import { SchoolClock } from '../../core/time/school-clock';
import { ChartComponent } from '../../shared/ui/chart.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ChartTheme } from '../dashboard/dashboard-charts';
import { DashboardChartComponent } from '../dashboard/dashboard-chart.component';
import {
  attendanceOption,
  bucketLabel,
  chartRows,
  clampRange,
  monthOptions,
  revenueOption,
  rowName,
  shiftPeriod,
} from './report-charts';
import { ReportsPage, fileName } from './reports.page';

type Option = Record<string, unknown>;

const theme: ChartTheme = {
  series: ['#100001', '#100002', '#100003', '#100004', '#100005'],
  text: '#777777',
};

const cell = (period: string | null, present: number, absent: number, rate: number | null) => ({
  period,
  present,
  late: 0,
  absent,
  excused: 0,
  rate,
});

const row = (
  id: string | null,
  name: string,
  present: number,
  absent: number,
): AttendanceRowDto => ({
  id,
  name,
  months: [
    cell('2027-02', present, absent, present / (present + absent)),
    cell('2027-03', 0, 0, null),
  ],
  total: cell(null, present, absent, present / (present + absent)),
});

const attendance = (over: Partial<AttendanceReportDto> = {}): AttendanceReportDto => ({
  from: '2027-02',
  to: '2027-03',
  by: 'Sport',
  sportId: null,
  coachId: null,
  periods: ['2027-02', '2027-03'],
  rows: [row('s1', 'Futbol', 3, 1), row('s2', '<b>Yüzme</b>', 1, 1)],
  total: row(null, '', 4, 2),
  ...over,
});

const occupancy = (): OccupancyReportDto => ({
  from: '2027-02',
  to: '2027-03',
  by: 'Group',
  sportId: null,
  coachId: null,
  periods: [
    { period: '2027-02', referenceDate: '2027-02-28' },
    { period: '2027-03', referenceDate: '2027-03-10' },
  ],
  rows: [
    {
      id: 'g1',
      name: 'U10 Futbol',
      months: [
        { period: '2027-02', enrolled: 3, capacity: 4, utilisation: 0.75 },
        { period: '2027-03', enrolled: 0, capacity: 0, utilisation: null },
      ],
    },
  ],
  total: {
    id: null,
    name: '',
    months: [
      { period: '2027-02', enrolled: 3, capacity: 4, utilisation: 0.75 },
      { period: '2027-03', enrolled: 0, capacity: 0, utilisation: null },
    ],
  },
});

const period = (p: string | null, accrued: string): RevenuePeriodDto => ({
  period: p,
  listFees: money('4800.00'),
  siblingDiscount: money('80.00'),
  groupChangeWaived: money('0.00'),
  accrued: money(accrued),
  collectedForPeriod: money('2620.00'),
  outstanding: money('2100.00'),
  collectedInMonth: money('2220.00'),
  collectionRate: 0.5551,
  charges: 6,
  siblingDiscountedCharges: 1,
});

const revenue = (): RevenueReportDto => ({
  from: '2027-02',
  to: '2027-03',
  today: '2027-03-10',
  dueDay: 10,
  periods: [period('2027-02', '4720.00'), period('2027-03', '4300.00')],
  total: period(null, '9020.00'),
  overdue: [
    { fromDays: 1, toDays: 30, dues: 5, amount: money('3800.00') },
    { fromDays: 31, toDays: 60, dues: 3, amount: money('2100.00') },
    { fromDays: 61, toDays: 90, dues: 0, amount: money('0.00') },
    { fromDays: 91, toDays: null, dues: 0, amount: money('0.00') },
  ],
  overdueDues: 8,
  overdueAmount: money('5900.00'),
  notYetDueDues: 4,
  notYetDueAmount: money('3800.00'),
});

describe('report helpers', () => {
  it('moves months across years', () => {
    expect(shiftPeriod('2027-01', -1)).toBe('2026-12');
    expect(shiftPeriod('2026-12', 1)).toBe('2027-01');
    expect(shiftPeriod('2027-03', -23)).toBe('2025-04');
  });

  it('offers the current month and the 23 before it, newest first', () => {
    const options = monthOptions('2027-03');
    expect(options).toHaveLength(24);
    expect(options[0].value).toBe('2027-03');
    expect(options[23].value).toBe('2025-04');
  });

  it('keeps a range within 12 months, the horizon and the current month', () => {
    expect(clampRange('2026-01', '2027-03', '2027-03', 'from')).toEqual({
      from: '2026-01',
      to: '2026-12',
    });
    expect(clampRange('2026-01', '2027-03', '2027-03', 'to')).toEqual({
      from: '2026-04',
      to: '2027-03',
    });
    expect(clampRange('2027-03', '2027-01', '2027-03', 'from')).toEqual({
      from: '2027-03',
      to: '2027-03',
    });
    expect(clampRange('2024-01', '2025-05', '2027-03', 'from')).toEqual({
      from: '2025-04',
      to: '2025-05',
    });
    expect(clampRange('2027-01', '2027-09', '2027-03', 'to')).toEqual({
      from: '2027-01',
      to: '2027-03',
    });
  });

  it('names a coach row without a coach', () => {
    expect(rowName({ id: null, name: '' }, 'Coach')).toBe(ui.reports.noCoach);
    expect(rowName({ id: 's1', name: ' ' }, 'Sport')).toBe('—');
  });

  it('charts the rows with the most counted records', () => {
    const rows = [
      row('a', 'A', 1, 0),
      row('b', 'B', 5, 5),
      row('c', 'C', 0, 0),
      row('d', 'D', 3, 0),
    ];
    expect(chartRows(rows, 2).map((r) => r.id)).toEqual(['b', 'd']);
  });

  it('draws the school as a solid line and rows dashed, with escaped tooltips and gaps for no data', () => {
    const option = attendanceOption(attendance(), theme) as Option;
    const series = option['series'] as {
      name: string;
      data: (number | null)[];
      lineStyle: { type?: string; width: number };
    }[];
    expect(series.map((s) => s.name)).toEqual([ui.reports.schoolTotal, 'Futbol', '<b>Yüzme</b>']);
    expect(series[0].data).toEqual([66.7, null]);
    expect(series[0].lineStyle.width).toBe(3);
    expect(series[1].lineStyle.type).toBe('dashed');
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    const text = tooltip([{ dataIndex: 0 }]);
    expect(text).toContain('&lt;b&gt;Yüzme&lt;/b&gt;: %50');
    expect(tooltip([{ dataIndex: 1 }])).toContain(`${ui.reports.schoolTotal}: —`);
  });

  it('labels money in the revenue tooltip from the amount text', () => {
    const option = revenueOption(revenue(), theme) as Option;
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    expect(tooltip([{ dataIndex: 0 }])).toContain('₺4.720,00');
    expect((option['series'] as { type: string }[]).map((s) => s.type)).toEqual([
      'bar',
      'bar',
      'line',
    ]);
  });

  it('names the ageing buckets', () => {
    expect(bucketLabel({ fromDays: 1, toDays: 30 })).toBe('1–30 gün');
    expect(bucketLabel({ fromDays: 91, toDays: null })).toBe('91 gün ve üzeri');
  });

  it('reads the file name from the download header', () => {
    expect(fileName('attachment; filename=gelir-raporu_2027-01_2027-03.csv')).toBe(
      'gelir-raporu_2027-01_2027-03.csv',
    );
    expect(fileName(null)).toBeNull();
  });
});

@Component({ selector: 'app-chart', template: '' })
class StubChartComponent {
  readonly options = input<unknown>();
  readonly height = input(0);
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('ReportsPage', () => {
  let fixture: ComponentFixture<ReportsPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { error: vi.fn(), success: vi.fn() };

  beforeEach(async () => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () => ({ matches: false, addEventListener: () => undefined }),
    });
    toast.error.mockReset();
    TestBed.configureTestingModule({
      imports: [ReportsPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: SchoolClock, useValue: { today: signal('2027-03-10') } },
        { provide: ToastService, useValue: toast },
      ],
    });
    TestBed.overrideComponent(DashboardChartComponent, {
      remove: { imports: [ChartComponent] },
      add: { imports: [StubChartComponent] },
    });
    TestBed.inject(ThemeService).set('light');
    fixture = TestBed.createComponent(ReportsPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const expectReport = (path: string) =>
    backend.expectOne((r) => r.url === `/api/v1/reports/${path}`);

  it('loads the last six months of attendance by group and shows the table with totals', async () => {
    const req = expectReport('attendance');
    expect(req.request.params.get('from')).toBe('2026-10');
    expect(req.request.params.get('to')).toBe('2027-03');
    expect(req.request.params.get('by')).toBe('group');
    req.flush(attendance());
    await settle(fixture);

    const table = root.querySelector('[data-testid="attendance-table"]') as HTMLElement;
    const rows = table.querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Futbol');
    expect(rows[0].textContent).toContain('%75');
    expect(rows[0].textContent).toContain('4 yoklama');
    expect(rows[1].querySelector('th')?.textContent).toContain('<b>Yüzme</b>');
    expect(table.querySelector('tfoot')?.textContent).toContain(ui.reports.schoolTotal);
    const figure = root.querySelector('[data-testid="chart-figure"]');
    expect(figure?.getAttribute('role')).toBe('img');
    expect(figure?.getAttribute('aria-label')).toContain('%66,7');
  });

  it('drills from a sport down to its groups and back', async () => {
    expectReport('attendance').flush(attendance());
    await settle(fixture);

    const drill = root.querySelector('button[aria-label^="Futbol"]') as HTMLButtonElement;
    expect(drill.textContent).toContain(ui.reports.drill);
    drill.click();
    await settle(fixture);
    const req = expectReport('attendance');
    expect(req.request.params.get('by')).toBe('group');
    expect(req.request.params.get('sportId')).toBe('s1');
    req.flush(attendance({ by: 'Group', rows: [row('g1', 'U10 Futbol', 3, 1)] }));
    await settle(fixture);
    expect(root.querySelector('[data-testid="report-filter"]')?.textContent).toContain(
      'Futbol branşının grupları',
    );

    (root.querySelector('[data-testid="report-filter"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(expectReport('attendance').request.params.has('sportId')).toBe(false);
  });

  it('switches tabs with the arrow keys and loads occupancy', async () => {
    expectReport('attendance').flush(attendance());
    await settle(fixture);
    const tab = root.querySelector('#report-tab-attendance') as HTMLButtonElement;
    tab.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
    await settle(fixture);

    expectReport('occupancy').flush(occupancy());
    await settle(fixture);
    expect(root.querySelector('#report-tab-occupancy')?.getAttribute('aria-selected')).toBe('true');
    const cells = root.querySelectorAll('[data-testid="occupancy-table"] tbody td');
    expect(cells[0].textContent?.trim()).toBe('3/4 · %75');
    expect(cells[1].textContent?.trim()).toBe('—');
    expect(root.textContent).toContain('28.02.2027');
  });

  it('shows revenue figures, the fee rules effect and the overdue ageing', async () => {
    expectReport('attendance').flush(attendance());
    await settle(fixture);
    (root.querySelector('#report-tab-revenue') as HTMLButtonElement).click();
    await settle(fixture);
    const req = expectReport('revenue');
    expect(req.request.params.has('by')).toBe(false);
    req.flush(revenue());
    await settle(fixture);

    expect(root.querySelector('[data-kpi="accrued"]')?.textContent).toContain('₺9.020,00');
    expect(root.querySelector('[data-kpi="sibling"]')?.textContent).toContain('₺80,00');
    expect(root.querySelector('[data-kpi="collected"]')?.textContent).toContain('%55,5');
    const overdue = root.querySelector('[data-testid="overdue-table"]') as HTMLElement;
    expect(overdue.textContent).toContain('31–60 gün');
    expect(overdue.textContent).toContain('₺5.900,00');
    expect(overdue.textContent).toContain(ui.reports.revenue.overdue.notYetDue);
    expect(root.querySelector('a[href="/dues"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="revenue-table"] tfoot')?.textContent).toContain(
      '₺9.020,00',
    );
  });

  it('downloads the current report as CSV with the same filters', async () => {
    expectReport('attendance').flush(attendance());
    await settle(fixture);
    const createObjectURL = vi.fn(() => 'blob:x');
    Object.assign(URL, { createObjectURL, revokeObjectURL: vi.fn() });
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => undefined);

    (root.querySelector('[data-testid="report-download"]') as HTMLButtonElement).click();
    const req = expectReport('attendance/csv');
    expect(req.request.responseType).toBe('blob');
    expect(req.request.params.get('by')).toBe('group');
    req.flush(new Blob(['x']), {
      headers: { 'Content-Disposition': 'attachment; filename=devam-raporu_2026-10_2027-03.csv' },
    });
    await settle(fixture);

    expect(createObjectURL).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
    click.mockRestore();
  });

  it('tells the admin to wait when the export limit is reached', async () => {
    expectReport('attendance').flush(attendance());
    await settle(fixture);
    (root.querySelector('[data-testid="report-download"]') as HTMLButtonElement).click();
    expectReport('attendance/csv').flush(new Blob([]), {
      status: 429,
      statusText: 'Too Many Requests',
    });
    await settle(fixture);
    expect(toast.error).toHaveBeenCalledWith(errorMessages['Request.TooManyRequests']);
  });

  it('shows an API error with a working retry', async () => {
    expectReport('attendance').flush(
      { type: 'about:blank', title: 'x', status: 400, code: 'Report.InvalidRange' },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(fixture);
    const alert = root.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain(errorMessages['Report.InvalidRange']);
    alert.querySelector('button')!.click();
    await settle(fixture);
    expectReport('attendance').flush(attendance());
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')).toBeNull();
  });
});

describe('reports route', () => {
  it('is a lazy-loaded School Admin page in the menu', async () => {
    const { routes } = await import('../../app.routes');
    const shell = routes.find((r) => r.path === '' && r.children)!;
    const route = shell
      .children!.flatMap((r) => r.children ?? [])
      .find((r) => r.path === 'reports')!;
    expect(route.title).toBe(ui.nav.reports);
    expect(await (route.loadComponent as () => Promise<unknown>)()).toBe(ReportsPage);
    const { navigationFor } = await import('../../layout/navigation');
    expect(
      navigationFor('SchoolAdmin')
        .flatMap((s) => s.items)
        .map((i) => i.path),
    ).toContain('/reports');
    expect(
      navigationFor('Coach')
        .flatMap((s) => s.items)
        .map((i) => i.path),
    ).not.toContain('/reports');
  });
});
