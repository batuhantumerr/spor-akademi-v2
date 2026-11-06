import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ui } from '../core/i18n/tr';
import { ShellComponent } from './shell.component';

/** jsdom has no matchMedia; report a phone-sized viewport (below lg). */
function stubMatchMedia(matches: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: query.includes('min-width') ? matches : false,
      media: query,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  );
}

describe('ShellComponent drawer', () => {
  let fixture: ComponentFixture<ShellComponent>;
  let root: HTMLElement;

  const aside = () => root.querySelector('aside') as HTMLElement;
  const menuButton = () =>
    root.querySelector(`button[aria-label="${ui.shell.openMenu}"]`) as HTMLButtonElement;
  const closeButton = () =>
    aside().querySelector(`button[aria-label="${ui.shell.closeMenu}"]`) as HTMLButtonElement;

  async function setup(desktop: boolean): Promise<void> {
    stubMatchMedia(desktop);
    TestBed.configureTestingModule({
      imports: [ShellComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(ShellComponent);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await fixture.whenStable();
  }

  afterEach(() => {
    root?.remove();
    vi.unstubAllGlobals();
  });

  it('keeps the closed mobile drawer out of the tab order', async () => {
    await setup(false);
    expect(aside().hasAttribute('inert')).toBe(true);
    expect(aside().getAttribute('aria-label')).toBe(ui.shell.mainMenu);
    expect(menuButton().getAttribute('aria-expanded')).toBe('false');
  });

  it('moves focus into the drawer when it opens', async () => {
    await setup(false);
    menuButton().click();
    await fixture.whenStable();

    expect(aside().hasAttribute('inert')).toBe(false);
    expect(menuButton().getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(closeButton());
  });

  it('closes on Escape and returns focus to the menu button', async () => {
    await setup(false);
    menuButton().click();
    await fixture.whenStable();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();

    expect(aside().hasAttribute('inert')).toBe(true);
    expect(root.querySelector('[data-testid="drawer-backdrop"]')).toBeNull();
    expect(document.activeElement).toBe(menuButton());
  });

  it('does not make the backdrop a tab stop', async () => {
    await setup(false);
    menuButton().click();
    await fixture.whenStable();

    const backdrop = root.querySelector('[data-testid="drawer-backdrop"]') as HTMLElement;
    expect(backdrop.tabIndex).toBe(-1);
    expect(backdrop.getAttribute('aria-hidden')).toBe('true');
  });

  it('leaves the sidebar interactive on desktop', async () => {
    await setup(true);
    expect(aside().hasAttribute('inert')).toBe(false);
  });
});
