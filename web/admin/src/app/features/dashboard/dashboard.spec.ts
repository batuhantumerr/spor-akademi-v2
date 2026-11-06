import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { DashboardDto, SportEnrollmentsDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { money } from '../../core/money/money';
import { ThemeService } from '../../core/theme/theme.service';
import { ChartComponent } from '../../shared/ui/chart.component';
import {
  ChartTheme,
  attendanceOption,
  billingOption,
  percent,
  periodLabel,
  sportsOption,
} from './dashboard-charts';
import { DashboardChartComponent } from './dashboard-chart.component';
import { DashboardPage } from './dashboard.page';

type Option = Record<string, unknown>;
interface Series {
  type: string;
  data: unknown[];
  itemStyle?: { color?: string };
}

const dashboard = (over: Partial<DashboardDto> = {}): DashboardDto => ({
  today: '2026-11-07',
  currentPeriod: '2026-11',
  kpis: {
    activeStudents: 42,
    activeGroups: 5,
    totalCapacity: 80,
    activeEnrollments: 60,
    capacityUtilisation: 0.75,
    collectedThisMonth: money('12500.50'),
    outstandingThisMonth: money('3000.00'),
    outstandingTotal: money('1234567.89'),
    attendanceRateLast30Days: '0.8571',
  },
  monthlyBilling: [
    { period: '2026-10', collected: money('10000.00'), due: money('12000.00') },
    { period: '2026-11', collected: money('12500.50'), due: money('15500.00') },
  ],
  weeklyAttendance: [
    { weekStart: '2026-10-26', weekEnd: '2026-11-01', counted: 40, rate: 0.9 },
    { weekStart: '2026-11-02', weekEnd: '2026-11-08', counted: 0, rate: null },
  ],
  enrollmentsBySport: [
    { sportId: 's1', sportName: 'Futbol', activeEnrollments: 40 },
    { sportId: 's2', sportName: 'Basketbol', activeEnrollments: '20' },
  ],
  groupUtilisation: [
    { groupId: 'g1', groupName: 'U12 Mavi', activeEnrollments: 20, capacity: 20, utilisation: 1 },
    { groupId: 'g2', groupName: 'U10', activeEnrollments: 9, capacity: 12, utilisation: 0.75 },
  ],
  todaySessions: { planned: 4, recorded: 1 },
  ...over,
});

const theme: ChartTheme = {
  series: ['#100001', '#100002', '#100003', '#100004', '#100005'],
  text: '#777777',
};

describe('dashboard percent', () => {
  it('never rounds a partial ratio up to %100 or down to %0', () => {
    expect(percent(0.996)).toBe('%99,6');
    expect(percent(0.9996)).toBe('%99,9');
    expect(percent(0.004)).toBe('%0,4');
    expect(percent(0.0004)).toBe('%0,1');
    expect(percent(1)).toBe('%100');
    expect(percent(0)).toBe('%0');
    expect(percent(1.25)).toBe('%125');
  });
});

describe('dashboard chart options', () => {
  it('keeps an unknown period as is and escapes it in the tooltip', () => {
    expect(periodLabel('bad<x>')).toBe('bad<x>');
    const option = billingOption(
      [{ period: 'bad<x>', collected: money('1.00'), due: money('2.00') }],
      theme,
    ) as Option;
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    expect(tooltip([{ dataIndex: 0 }])).toContain('bad&lt;x&gt;');
    const axis = (option['yAxis'] as { axisLabel: { formatter: (v: number) => string } }).axisLabel
      .formatter;
    expect(axis(12500)).toBe('₺12.500');
  });

  it('plots collected as bars and due as a line, formatting tooltips from the amount text', () => {
    const months = dashboard().monthlyBilling;
    const option = billingOption(months, theme) as Option;
    const [collected, due] = option['series'] as Series[];

    expect(collected.type).toBe('bar');
    expect(collected.data).toEqual([10000, 12500.5]);
    expect(collected.itemStyle?.color).toBe('#100001');
    expect(due.type).toBe('line');
    expect(due.data).toEqual([12000, 15500]);
    expect(due.itemStyle?.color).toBe('#100002');
    expect((option['xAxis'] as Option)['data']).toEqual([
      periodLabel('2026-10'),
      periodLabel('2026-11'),
    ]);
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    const html = tooltip([{ dataIndex: 1 }]);
    expect(html).toContain('₺12.500,50');
    expect(html).toContain('₺15.500,00');
  });

  it('plots weekly rates in percent with gaps for weeks without records', () => {
    const option = attendanceOption(dashboard().weeklyAttendance, theme) as Option;
    const [line] = option['series'] as Series[];
    expect(line.data).toEqual([90, null]);
    expect(line.itemStyle?.color).toBe('#100003');
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    expect(tooltip([{ dataIndex: 1 }])).toContain(ui.dashboard.attendance.noRecords);
  });

  it('cycles the token colours across sports and escapes names in tooltips', () => {
    const sports: SportEnrollmentsDto[] = Array.from({ length: 6 }, (_, i) => ({
      sportId: `s${i}`,
      sportName: i === 0 ? '<b>Yüzme</b>' : `Spor ${i}`,
      activeEnrollments: 10 - i,
    }));
    const option = sportsOption(sports, theme) as Option;
    const [pie] = option['series'] as {
      data: { name: string; value: number; itemStyle: { color: string } }[];
    }[];
    expect(pie.data.map((d) => d.value)).toEqual([10, 9, 8, 7, 6, 5]);
    expect(pie.data[4].itemStyle.color).toBe('#100005');
    expect(pie.data[5].itemStyle.color).toBe('#100001');
    const tooltip = (option['tooltip'] as { formatter: (p: unknown) => string }).formatter;
    expect(tooltip({ dataIndex: 0 })).toBe('&lt;b&gt;Yüzme&lt;/b&gt;: 10');
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
}

describe('DashboardPage', () => {
  let fixture: ComponentFixture<DashboardPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  let tokens: HTMLStyleElement;

  beforeEach(() => {
    // jsdom has no matchMedia; ThemeService reads the system preference from it.
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () => ({ matches: false, addEventListener: () => undefined }),
    });
    tokens = document.createElement('style');
    tokens.textContent =
      ':root { --chart-1: #2563eb; --chart-2: #d97706; } .dark { --chart-1: #60a5fa; --chart-2: #ffb703; }';
    document.head.appendChild(tokens);
    TestBed.configureTestingModule({
      imports: [DashboardPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.overrideComponent(DashboardChartComponent, {
      remove: { imports: [ChartComponent] },
      add: { imports: [StubChartComponent] },
    });
    TestBed.inject(ThemeService).set('light');
    fixture = TestBed.createComponent(DashboardPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    backend.verify();
    tokens.remove();
    document.documentElement.classList.remove('dark', 'light');
    try {
      localStorage.removeItem('sa-theme');
    } catch {
      // ignore
    }
  });

  const request = () => backend.expectOne((r) => r.url === '/api/v1/dashboard');
  const kpi = (key: string) => root.querySelector(`[data-kpi="${key}"]`)?.textContent ?? '';

  it('shows a skeleton while loading', () => {
    expect(root.querySelector('[data-testid="dashboard-skeleton"]')).not.toBeNull();
    expect(root.querySelector('[role="status"]')?.textContent).toContain(ui.dashboard.loading);
    request().flush(dashboard());
  });

  it('renders the KPI cards with money from the amount text', async () => {
    request().flush(dashboard());
    await settle(fixture);

    expect(root.textContent).toContain('07.11.2026');
    expect(kpi('students')).toContain('42');
    expect(kpi('groups')).toContain('60 aktif kayıt');
    expect(kpi('capacity')).toContain('%75');
    expect(kpi('capacity')).toContain('60 / 80 kontenjan');
    expect(kpi('collected')).toContain('₺12.500,50');
    expect(kpi('outstanding')).toContain('₺3.000,00');
    expect(kpi('outstandingTotal')).toContain('₺1.234.567,89');
    expect(kpi('attendance')).toContain('%85,7');
    expect(kpi('today')).toContain('1 / 4');
    expect(root.querySelector('[data-kpi="today"] a')?.getAttribute('href')).toBe('/attendance');
  });

  it('shows null ratios as a dash', async () => {
    const data = dashboard();
    request().flush(
      dashboard({
        kpis: { ...data.kpis, capacityUtilisation: null, attendanceRateLast30Days: null },
        todaySessions: { planned: 0, recorded: 0 },
      }),
    );
    await settle(fixture);

    expect(kpi('capacity')).toContain('—');
    expect(kpi('capacity')).toContain(ui.dashboard.kpi.noData);
    expect(kpi('attendance')).toContain('—');
    expect(kpi('attendance')).not.toContain('%');
    expect(kpi('today')).toContain(ui.dashboard.today.none);
  });

  it('describes each chart and offers its data as a table', async () => {
    request().flush(dashboard());
    await settle(fixture);

    const figures = [...root.querySelectorAll('[data-testid="chart-figure"]')];
    expect(figures).toHaveLength(3);
    expect(figures[0].getAttribute('role')).toBe('img');
    expect(figures[0].getAttribute('aria-label')).toContain('tahsil edilen ₺12.500,50');
    expect(figures[2].getAttribute('aria-label')).toContain('Futbol 40, Basketbol 20');

    const toggle = root.querySelector('button[aria-controls="billing-table"]') as HTMLButtonElement;
    const table = root.querySelector('#billing-table') as HTMLElement;
    expect(table.hidden).toBe(true);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    await settle(fixture);
    expect(table.hidden).toBe(false);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(table.textContent).toContain('₺15.500,00');
    const weekRows = root.querySelectorAll('#attendance-table tbody tr');
    expect(weekRows[0].textContent).toContain('%90');
    expect(weekRows[1].textContent).toContain('02.11.2026 – 08.11.2026');
    expect(weekRows[1].textContent).toContain(ui.dashboard.attendance.noRecords);
    expect(weekRows[1].querySelectorAll('td')[1].textContent?.trim()).toBe('0');
  });

  it('gives every table toggle its own name', async () => {
    request().flush(dashboard());
    await settle(fixture);
    const labels = [...root.querySelectorAll('button[aria-controls$="-table"]')].map((b) =>
      b.getAttribute('aria-label'),
    );
    expect(labels).toEqual([
      `${ui.dashboard.showTable}: ${ui.dashboard.billing.title}`,
      `${ui.dashboard.showTable}: ${ui.dashboard.attendance.title}`,
      `${ui.dashboard.showTable}: ${ui.dashboard.sports.title}`,
    ]);
    (root.querySelector('button[aria-controls="billing-table"]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(
      root.querySelector('button[aria-controls="billing-table"]')?.getAttribute('aria-label'),
    ).toBe(`${ui.dashboard.hideTable}: ${ui.dashboard.billing.title}`);
  });

  it('lists the fullest groups with a non-colour "full" cue and links', async () => {
    request().flush(dashboard());
    await settle(fixture);

    const items = root.querySelectorAll('[data-testid="group-utilisation"] li');
    expect(items).toHaveLength(2);
    expect(items[0].textContent).toContain('20 / 20');
    expect(items[0].textContent).toContain(ui.dashboard.groups.full);
    expect(items[0].querySelector('a')?.getAttribute('href')).toBe('/groups/g1');
    expect(items[1].textContent).toContain('%75');
    expect(items[1].textContent).not.toContain(ui.dashboard.groups.full);
  });

  it('shows a message instead of the donut when no sport has enrollments', async () => {
    request().flush(dashboard({ enrollmentsBySport: [] }));
    await settle(fixture);
    expect(root.textContent).toContain(ui.dashboard.sports.empty);
    expect(root.querySelectorAll('[data-testid="chart-figure"]')).toHaveLength(2);
  });

  it('reads chart colours from the tokens and rebuilds them when the theme flips', async () => {
    request().flush(dashboard());
    await settle(fixture);
    const page = fixture.componentInstance;
    const colour = () =>
      ((page.billing().options as Option)['series'] as Series[])[0].itemStyle?.color;

    expect(colour()).toBe('#2563eb');
    TestBed.inject(ThemeService).set('dark');
    expect(colour()).toBe('#60a5fa');
    TestBed.inject(ThemeService).set('light');
    expect(colour()).toBe('#2563eb');
  });

  it('treats sports with only zero counts as empty and labels blank names', async () => {
    request().flush(
      dashboard({
        enrollmentsBySport: [
          { sportId: 's1', sportName: ' ', activeEnrollments: 0 },
          { sportId: 's2', sportName: 'Futbol', activeEnrollments: '0' },
        ],
      }),
    );
    await settle(fixture);
    expect(root.textContent).toContain(ui.dashboard.sports.empty);
    expect(root.querySelector('#sports-table')).toBeNull();
  });

  it('labels a blank sport name with a dash', async () => {
    request().flush(
      dashboard({ enrollmentsBySport: [{ sportId: 's1', sportName: '', activeEnrollments: 3 }] }),
    );
    await settle(fixture);
    expect(root.querySelector('#sports-table th[scope="row"]')?.textContent?.trim()).toBe('—');
  });

  it('keeps the dashboard when only billing data exists', async () => {
    const data = dashboard();
    request().flush(dashboard({ kpis: { ...data.kpis, activeStudents: 0, activeGroups: 0 } }));
    await settle(fixture);
    expect(root.querySelector('[data-testid="dashboard-empty"]')).toBeNull();
    expect(kpi('collected')).toContain('₺12.500,50');
  });

  it('shows an empty state for a school without students or groups', async () => {
    const data = dashboard();
    request().flush(
      dashboard({
        kpis: {
          ...data.kpis,
          activeStudents: 0,
          activeGroups: '0',
          collectedThisMonth: money('0.00'),
          outstandingThisMonth: money('0.00'),
          outstandingTotal: money('0.00'),
        },
        monthlyBilling: [{ period: '2026-11', collected: money('0.00'), due: money('0') }],
      }),
    );
    await settle(fixture);

    expect(root.querySelector('[data-testid="dashboard-empty"]')).not.toBeNull();
    expect(root.querySelector('a[href="/students/new"]')).not.toBeNull();
    expect(root.querySelector('[data-kpi]')).toBeNull();
  });

  it('shows an error with a working retry', async () => {
    request().flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    const alert = root.querySelector('[role="alert"]') as HTMLElement;
    expect(alert.textContent).toContain('Sunucuya ulaşılamıyor');

    alert.querySelector('button')!.click();
    await settle(fixture);
    request().flush(dashboard());
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(kpi('students')).toContain('42');
  });
});

describe('dashboard route', () => {
  it('is the lazy-loaded School Admin home', async () => {
    const { routes } = await import('../../app.routes');
    const shell = routes.find((r) => r.path === '' && r.children)!;
    const route = shell
      .children!.flatMap((r) => r.children ?? [])
      .find((r) => r.path === 'dashboard')!;
    expect(route.title).toBe(ui.nav.dashboard);
    const component = await (route.loadComponent as () => Promise<unknown>)();
    expect(component).toBe(DashboardPage);
  });
});
