import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ScheduleSlotDto, WarningDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { SlotDraft } from '../../core/schedule/schedule';
import { ScheduleEditorComponent } from './schedule-editor.component';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

describe('ScheduleEditorComponent', () => {
  let fixture: ComponentFixture<ScheduleEditorComponent>;
  let root: HTMLElement;
  let saved: SlotDraft[][];

  async function render(
    slots: ScheduleSlotDto[],
    extra: { warnings?: WarningDto[]; error?: string } = {},
  ): Promise<void> {
    fixture = TestBed.createComponent(ScheduleEditorComponent);
    fixture.componentRef.setInput('slots', slots);
    fixture.componentRef.setInput('warnings', extra.warnings ?? []);
    fixture.componentRef.setInput('error', extra.error ?? null);
    saved = [];
    fixture.componentInstance.save.subscribe((s) => saved.push(s));
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  }

  afterEach(() => root?.remove());

  const field = (id: string) => root.querySelector(`#${id}`) as HTMLInputElement;
  const button = (text: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.includes(text)) as
      HTMLButtonElement | undefined;
  async function type(id: string, value: string): Promise<void> {
    field(id).value = value;
    field(id).dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  async function submit(): Promise<void> {
    button(ui.groupDetail.saveSchedule)!.click();
    await settle(fixture);
  }

  it('shows the slots in Turkish week order with 24h times', async () => {
    await render([
      { day: 'Thursday', start: '17:00', end: '18:30' },
      { day: 'Tuesday', start: '17:00', end: '18:30' },
    ]);
    expect((field('slot-day-0') as unknown as HTMLSelectElement).value).toBe('Tuesday');
    expect(field('slot-start-1').value).toBe('17:00');
    expect(root.querySelectorAll('legend')[0].textContent).toContain('1. saat');
  });

  it('adds a slot on the next day with the same times and focuses it', async () => {
    await render([{ day: 'Tuesday', start: '17:00', end: '18:30' }]);
    button(ui.groupDetail.addSlot)!.click();
    await settle(fixture);

    expect((field('slot-day-1') as unknown as HTMLSelectElement).value).toBe('Wednesday');
    expect(field('slot-end-1').value).toBe('18:30');
    expect(document.activeElement).toBe(field('slot-day-1'));
  });

  it('blocks an overlapping schedule and focuses the first problem', async () => {
    await render([
      { day: 'Monday', start: '17:00', end: '18:30' },
      { day: 'Wednesday', start: '18:00', end: '19:00' },
    ]);
    const day = field('slot-day-1') as unknown as HTMLSelectElement;
    day.value = 'Monday';
    day.dispatchEvent(new Event('change'));
    await settle(fixture);
    await submit();

    expect(saved).toEqual([]);
    expect(root.querySelector('#slot-end-0-error')?.textContent).toContain(
      errorMessages['Schedule.SlotOverlap'],
    );
    expect(field('slot-end-0').getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(field('slot-end-0'));
  });

  it('blocks an end before the start and a malformed time', async () => {
    await render([{ day: 'Monday', start: '17:00', end: '18:00' }]);
    await type('slot-start-0', '7.00');
    await type('slot-end-0', '06:00');
    await submit();

    expect(saved).toEqual([]);
    expect(root.querySelector('#slot-start-0-error')?.textContent).toContain(
      ui.groupDetail.timeInvalid,
    );

    await type('slot-start-0', '07:00');
    await submit();
    expect(root.querySelector('#slot-end-0-error')?.textContent).toContain(
      errorMessages['TimeRange.Invalid'],
    );
    expect(saved).toEqual([]);
  });

  it('emits the sorted slots when valid', async () => {
    await render([{ day: 'Friday', start: '10:00', end: '11:00' }]);
    button(ui.groupDetail.addSlot)!.click();
    await settle(fixture);
    const day = field('slot-day-1') as unknown as HTMLSelectElement;
    day.value = 'Monday';
    day.dispatchEvent(new Event('change'));
    await type('slot-start-1', '09:00');
    await type('slot-end-1', '09:45');
    await submit();

    expect(saved).toEqual([
      [
        { day: 'Monday', start: '09:00', end: '09:45' },
        { day: 'Friday', start: '10:00', end: '11:00' },
      ],
    ]);
  });

  it('removes a slot', async () => {
    await render([
      { day: 'Monday', start: '17:00', end: '18:00' },
      { day: 'Tuesday', start: '17:00', end: '18:00' },
    ]);
    (
      root.querySelector(
        `button[aria-label="${ui.groupDetail.removeSlot}: 1. saat"]`,
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);
    await submit();
    expect(saved).toEqual([[{ day: 'Tuesday', start: '17:00', end: '18:00' }]]);
  });

  it('shows facility overlaps as a non-blocking notice with the other group', async () => {
    await render([{ day: 'Monday', start: '17:00', end: '18:00' }], {
      warnings: [
        { code: 'Schedule.FacilityOverlap', relatedId: 'g2', args: { groupName: 'U12 Mavi' } },
      ],
    });

    const notice = root.querySelector('[role="status"]') as HTMLElement;
    expect(notice.textContent).toContain(ui.groupDetail.warningsTitle);
    expect(notice.textContent).toContain('U12 Mavi');
    expect(notice.querySelector('.text-warning, .bg-warning\\/10')).toBeDefined();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    // The editor stays usable.
    expect(button(ui.groupDetail.saveSchedule)!.disabled).toBe(false);
  });

  it('shows an API error as an alert', async () => {
    await render([{ day: 'Monday', start: '17:00', end: '18:00' }], {
      error: 'Antrenörün bu saatte başka bir grubu var: U10 Kırmızı.',
    });
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('U10 Kırmızı');
  });

  it('is read-only for archived groups', async () => {
    fixture = TestBed.createComponent(ScheduleEditorComponent);
    fixture.componentRef.setInput('slots', [{ day: 'Monday', start: '17:00', end: '18:00' }]);
    fixture.componentRef.setInput('readonly', true);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);

    expect(field('slot-start-0').disabled).toBe(true);
    expect(button(ui.groupDetail.saveSchedule)).toBeUndefined();
  });
});
