import { TestBed } from '@angular/core/testing';

import { ScrollRegions } from './scroll-regions';

/** jsdom has no layout: the box's widths and the observers are stubbed. */
function box(html: string, scrollWidth: number): HTMLElement {
  const element = document.createElement('div');
  element.className = 'table-shell';
  element.innerHTML = html;
  Object.defineProperty(element, 'clientWidth', { configurable: true, value: 300 });
  Object.defineProperty(element, 'scrollWidth', { configurable: true, value: scrollWidth });
  document.body.appendChild(element);
  return element;
}

const frame = () => new Promise((resolve) => setTimeout(resolve, 30));

describe('ScrollRegions (P3-11, DEC-442)', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        readonly observe = vi.fn();
        readonly disconnect = vi.fn();
      },
    );
    TestBed.configureTestingModule({});
    TestBed.runInInjectionContext(() => TestBed.inject(ScrollRegions).watch());
  });

  afterEach(() => {
    document.body.innerHTML = '';
    vi.unstubAllGlobals();
  });

  it('makes a sideways scrolling table without controls a named Tab stop', async () => {
    const table = box('<table><caption>Sürümler</caption></table>', 500);
    await frame();
    expect(table.getAttribute('tabindex')).toBe('0');
    expect(table.getAttribute('role')).toBe('region');
    expect(table.getAttribute('aria-label')).toBe('Sürümler');
  });

  it('leaves tables that fit, tables with controls and boxes the page set up itself', async () => {
    const fits = box('<table></table>', 300);
    const controls = box('<table><tr><td><button>İade et</button></td></tr></table>', 500);
    const own = box('<table></table>', 500);
    own.setAttribute('tabindex', '0');
    own.setAttribute('aria-label', 'Ödemeler');
    await frame();
    expect(fits.hasAttribute('tabindex')).toBe(false);
    expect(controls.hasAttribute('tabindex')).toBe(false);
    expect(own.getAttribute('aria-label')).toBe('Ödemeler');
    expect(own.hasAttribute('data-scroll-stop')).toBe(false);
  });
});
