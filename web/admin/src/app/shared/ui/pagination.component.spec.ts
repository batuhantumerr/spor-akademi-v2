import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ui } from '../../core/i18n/tr';
import { PaginationComponent } from './pagination.component';

describe('PaginationComponent', () => {
  let fixture: ComponentFixture<PaginationComponent>;
  let root: HTMLElement;

  async function render(page: number, totalPages: number, disabled = false): Promise<void> {
    fixture = TestBed.createComponent(PaginationComponent);
    fixture.componentRef.setInput('page', page);
    fixture.componentRef.setInput('totalPages', totalPages);
    fixture.componentRef.setInput('totalCount', 1234);
    fixture.componentRef.setInput('disabled', disabled);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  }

  const button = (label: string) =>
    root.querySelector(`button[aria-label="${label}"]`) as HTMLButtonElement;

  it('renders nothing for a single page', async () => {
    await render(1, 1);
    expect(root.querySelector('nav')).toBeNull();
  });

  it('uses the Turkish texts from tr.ts', async () => {
    await render(2, 5);
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe(ui.common.pagination);
    expect(root.textContent).toContain('Toplam 1.234 kayıt');
    expect(root.textContent).toContain('2 / 5');
    expect(button(ui.common.previousPage)).not.toBeNull();
    expect(button(ui.common.nextPage)).not.toBeNull();
  });

  it('emits the neighbouring pages and disables at the edges', async () => {
    await render(1, 3);
    const pages: number[] = [];
    fixture.componentInstance.pageChange.subscribe((p) => pages.push(p));

    expect(button(ui.common.previousPage).disabled).toBe(true);
    button(ui.common.nextPage).click();
    expect(pages).toEqual([2]);
  });

  it('disables both buttons while a request is in flight', async () => {
    await render(2, 3, true);
    expect(button(ui.common.previousPage).disabled).toBe(true);
    expect(button(ui.common.nextPage).disabled).toBe(true);
  });
});
