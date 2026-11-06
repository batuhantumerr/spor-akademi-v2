import { TestBed } from '@angular/core/testing';

import { TOAST_DURATION_MS, ToastService, ToastsComponent } from './toast.service';

describe('ToastService', () => {
  let toasts: ToastService;

  beforeEach(() => {
    vi.useFakeTimers();
    toasts = TestBed.inject(ToastService);
  });

  afterEach(() => vi.useRealTimers());

  it('dismisses a toast after its duration', () => {
    toasts.success('Kaydedildi');
    vi.advanceTimersByTime(TOAST_DURATION_MS.success - 1);
    expect(toasts.toasts().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(toasts.toasts().length).toBe(0);
  });

  it('keeps errors longer than successes', () => {
    toasts.error('Hata');
    vi.advanceTimersByTime(TOAST_DURATION_MS.success);
    expect(toasts.toasts().length).toBe(1);
    vi.advanceTimersByTime(TOAST_DURATION_MS.error - TOAST_DURATION_MS.success);
    expect(toasts.toasts().length).toBe(0);
  });

  it('pauses while held and resumes with the remaining time', () => {
    toasts.success('Kaydedildi');
    vi.advanceTimersByTime(1000);
    toasts.pause();
    vi.advanceTimersByTime(60_000);
    expect(toasts.toasts().length).toBe(1);

    toasts.resume();
    vi.advanceTimersByTime(TOAST_DURATION_MS.success - 1000 - 1);
    expect(toasts.toasts().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(toasts.toasts().length).toBe(0);
  });

  it('clears the timer when dismissed by hand', () => {
    toasts.success('Kaydedildi');
    const id = toasts.toasts()[0].id;
    toasts.dismiss(id);
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('ToastsComponent', () => {
  it('announces successes in one polite region and errors as alerts', async () => {
    const fixture = TestBed.createComponent(ToastsComponent);
    const service = TestBed.inject(ToastService);
    service.success('Branş eklendi.');
    service.error('Sunucuya ulaşılamıyor.');
    await fixture.whenStable();

    const root = fixture.nativeElement as HTMLElement;
    const status = root.querySelectorAll('[role="status"]');
    expect(status.length).toBe(1);
    expect(status[0].textContent).toContain('Branş eklendi.');
    expect(status[0].querySelector('[role]')).toBeNull();
    const alerts = root.querySelectorAll('[role="alert"]');
    expect(alerts.length).toBe(1);
    expect(alerts[0].textContent).toContain('Sunucuya ulaşılamıyor.');
    for (const toast of service.toasts()) {
      service.dismiss(toast.id);
    }
  });
});
