import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import AxeBuilder from '@axe-core/playwright';
import { Page, TestInfo, expect } from '@playwright/test';

/**
 * WCAG 2.2 AA audit helpers (P3-11, DEC-436..DEC-445; docs/erisilebilirlik-denetimi.md).
 *
 * Every important screen is scanned in eight variants: Turkish and English, light and dark theme, a desktop and a
 * 360 px phone viewport. The language is switched through the app's own switcher, the theme follows the emulated
 * `prefers-color-scheme` (the apps' default "system" preference) and the viewport is resized in place, so one page
 * and one session cover all variants (logins are rate limited, NFR-SEC-14).
 *
 * Besides axe (WCAG 2.0/2.1/2.2 A and AA tags, every impact recorded), each screen gets scripted checks of criteria
 * axe cannot see: keyboard focus is visible and not entirely hidden (2.4.7, 2.4.11), the page reflows at 320 px
 * (1.4.10) and survives the text spacing override (1.4.12).
 *
 * Only serious and critical axe findings and the scripted checks' failures fail the test (DEC-437); moderate and
 * minor ones go to the report, which every run writes to test-results/a11y/ and attaches to the test.
 */

export type Language = 'tr' | 'en';
export type Theme = 'light' | 'dark';
export type Width = 'desktop' | 'mobile';

export interface Variant {
  language: Language;
  theme: Theme;
  width: Width;
}

const viewports: Record<Width, { width: number; height: number }> = {
  desktop: { width: 1280, height: 800 },
  mobile: { width: 360, height: 780 },
};

export const allVariants: Variant[] = (['tr', 'en'] as const).flatMap((language) =>
  (['light', 'dark'] as const).flatMap((theme) =>
    (['desktop', 'mobile'] as const).map((width) => ({ language, theme, width })),
  ),
);

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

export interface Finding {
  screen: string;
  variant: string;
  source: 'axe' | 'keyboard' | 'reflow' | 'text-spacing';
  rule: string;
  impact: string;
  blocking: boolean;
  help: string;
  targets: string[];
}

/** Collects the findings of one spec file; written out by {@link writeReport}. */
export class A11yAudit {
  readonly findings: Finding[] = [];
  readonly screens = new Map<string, Set<string>>();

  constructor(private readonly app: 'admin' | 'parent') {}

  /** Runs the whole matrix for the page as it is now and fails on blocking findings. */
  async screen(page: Page, name: string, options: ScreenOptions = {}): Promise<void> {
    const variants = options.variants ?? allVariants;
    const start = await currentLanguage(page);
    const before = this.findings.length;
    const started = Date.now();
    for (const variant of variants) {
      await applyVariant(page, variant);
      await options.prepare?.(page, variant);
      await this.axe(page, name, variant, options.exclude ?? []);
    }
    if (options.keyboard !== false) {
      for (const width of ['desktop', 'mobile'] as const) {
        const variant: Variant = { language: start, theme: 'light', width };
        await applyVariant(page, variant);
        await this.keyboard(page, name, variant, options.keyboardScope);
      }
    }
    if (options.reflow !== false) {
      await applyVariant(page, { language: start, theme: 'light', width: 'mobile' });
      await this.reflow(page, name);
      await this.textSpacing(page, name);
    }
    await applyVariant(page, { language: start, theme: 'light', width: 'desktop' });
    console.log(
      `[a11y] ${this.app}/${name}: ${this.findings.length - before} finding(s), ${Date.now() - started} ms`,
    );
    const blocking = this.findings
      .slice(before)
      .filter((f) => f.blocking)
      .map(
        (f) =>
          `${f.variant} ${f.source}/${f.rule} (${f.impact}): ${f.targets.join(' | ')} — ${f.help}`,
      );
    expect.soft(blocking, `WCAG 2.2 AA blocking findings on ${name}`).toEqual([]);
  }

  private record(
    name: string,
    variant: Variant | string,
    finding: Omit<Finding, 'screen' | 'variant'>,
  ): void {
    const label = typeof variant === 'string' ? variant : variantLabel(variant);
    if (!this.screens.has(name)) this.screens.set(name, new Set());
    this.screens.get(name)!.add(label);
    this.findings.push({ screen: name, variant: label, ...finding });
  }

  private async axe(page: Page, name: string, variant: Variant, exclude: string[]): Promise<void> {
    let builder = new AxeBuilder({ page }).withTags(wcagTags);
    for (const selector of exclude) builder = builder.exclude(selector);
    const results = await builder.analyze();
    if (!this.screens.has(name)) this.screens.set(name, new Set());
    this.screens.get(name)!.add(variantLabel(variant));
    for (const v of results.violations) {
      const impact = v.impact ?? 'minor';
      this.record(name, variant, {
        source: 'axe',
        rule: v.id,
        impact,
        blocking: impact === 'serious' || impact === 'critical',
        help: v.help,
        targets: v.nodes.slice(0, 5).map((n) => `${n.target.join(' ')}`),
      });
    }
  }

  /**
   * Tabs through the page from the top: every stop must show a visible change of style (2.4.7) and must not be
   * hidden entirely behind sticky or fixed content such as the header or the bottom tab bar (2.4.11). The walk stops
   * when focus comes back to where it started, or after `maxStops`.
   */
  private async keyboard(
    page: Page,
    name: string,
    variant: Variant,
    scope: string | undefined,
    maxStops = 70,
  ): Promise<void> {
    await page.mouse.move(0, 0);
    // Final styles at once: a focus ring that fades in would otherwise read as "no change".
    const instant = await page.addStyleTag({
      content:
        '*, *::before, *::after { transition: none !important; animation: none !important; }',
    });
    await page.evaluate(() => {
      (document.activeElement as HTMLElement | null)?.blur();
      window.scrollTo(0, 0);
      document.querySelector('main')?.scrollTo(0, 0);
    });
    // One entry per element. A native date/month input is several Tab stops (its segments); it counts as showing
    // focus when any of its stops does.
    const stops = new Map<string, FocusProbe>();
    let first: string | undefined;
    for (let stop = 0; stop < maxStops; stop++) {
      await page.keyboard.press('Tab');
      const probe = await page.evaluate(probeFocus, scope ?? null);
      if (!probe) continue;
      if (first === undefined) first = probe.id;
      else if (probe.id === first) break;
      const known = stops.get(probe.id);
      if (known) known.indicator ||= probe.indicator;
      else stops.set(probe.id, probe);
    }
    for (const probe of stops.values()) {
      if (!probe.inScope) continue;
      if (!probe.indicator) {
        // A native date/time input's stops include Chromium's own picker button, whose focus ring the page's styles
        // cannot see; it is reported for a manual look instead of failing (docs/erisilebilirlik-denetimi.md).
        this.record(name, variant, {
          source: 'keyboard',
          rule: probe.native ? 'focus-visible-native-input' : 'focus-visible',
          impact: probe.native ? 'minor' : 'serious',
          blocking: !probe.native,
          help: 'WCAG 2.4.7: the focused element shows no visible change',
          targets: [probe.description],
        });
      }
      if (probe.obscured === 'entirely') {
        this.record(name, variant, {
          source: 'keyboard',
          rule: 'focus-not-obscured',
          impact: 'serious',
          blocking: true,
          help: 'WCAG 2.4.11: the focused element is hidden entirely by other content',
          targets: [`${probe.description} under ${probe.coveredBy}`],
        });
      } else if (probe.obscured === 'partly') {
        this.record(name, variant, {
          source: 'keyboard',
          rule: 'focus-partly-obscured',
          impact: 'minor',
          blocking: false,
          help: 'WCAG 2.4.12 (AAA): part of the focused element is covered',
          targets: [`${probe.description} under ${probe.coveredBy}`],
        });
      }
    }
    await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await instant.evaluate((node) => node.remove());
  }

  /** 1.4.10: at 320 CSS px nothing but data tables (which scroll in their own box) may need horizontal scrolling. */
  private async reflow(page: Page, name: string): Promise<void> {
    await page.setViewportSize({ width: 320, height: 640 });
    // Charts redraw on a throttled resize observer; give them time to take the new width.
    await page.waitForTimeout(500);
    await settle(page);
    const overflow = await page.evaluate(horizontalOverflow);
    if (overflow.length > 0) {
      this.record(name, '320px', {
        source: 'reflow',
        rule: 'reflow-320',
        impact: 'serious',
        blocking: true,
        help: 'WCAG 1.4.10: content wider than a 320 px viewport outside a scrollable data table',
        targets: overflow,
      });
    }
  }

  /** 1.4.12: with the criterion's spacing override no text may be cut off and the page must still reflow. */
  private async textSpacing(page: Page, name: string): Promise<void> {
    const style = await page.addStyleTag({
      content:
        '* { line-height: 1.5 !important; letter-spacing: 0.12em !important; word-spacing: 0.16em !important; }' +
        ' p { margin-bottom: 2em !important; }',
    });
    await settle(page);
    const clipped = await page.evaluate(clippedText);
    const overflow = await page.evaluate(horizontalOverflow);
    await style.evaluate((node) => node.remove());
    await page.setViewportSize(viewports.mobile);
    if (clipped.length > 0 || overflow.length > 0) {
      this.record(name, '320px+spacing', {
        source: 'text-spacing',
        rule: 'text-spacing',
        impact: 'serious',
        blocking: true,
        help: 'WCAG 1.4.12: text is cut off or the page overflows with increased text spacing',
        targets: [...clipped, ...overflow],
      });
    }
  }

  /** Writes test-results/a11y/<app>.json and .md and attaches them; returns the markdown. */
  async writeReport(testInfo: TestInfo): Promise<string> {
    const dir = join(testInfo.project.outputDir, 'a11y');
    mkdirSync(dir, { recursive: true });
    const grouped = new Map<string, { finding: Finding; variants: Set<string> }>();
    for (const f of this.findings) {
      const key = `${f.screen}|${f.source}|${f.rule}|${f.targets.join(',')}`;
      const entry = grouped.get(key) ?? { finding: f, variants: new Set<string>() };
      entry.variants.add(f.variant);
      grouped.set(key, entry);
    }
    const lines = [
      `# WCAG 2.2 AA — ${this.app}`,
      '',
      `Screens: ${this.screens.size}; axe scans: ${[...this.screens.values()].reduce((n, s) => n + s.size, 0)}.`,
      '',
      '| Screen | Source | Rule | Impact | Blocking | Variants | Targets |',
      '| --- | --- | --- | --- | --- | --- | --- |',
      ...[...grouped.values()].map(
        ({ finding: f, variants }) =>
          `| ${f.screen} | ${f.source} | ${f.rule} | ${f.impact} | ${f.blocking ? 'yes' : 'no'} | ${[...variants].join(', ')} | ${f.targets.join('<br>').replaceAll('|', '\\|')} |`,
      ),
      '',
      `Screens without findings: ${[...this.screens.keys()].filter((s) => !this.findings.some((f) => f.screen === s)).join(', ') || '—'}`,
    ];
    const markdown = lines.join('\n');
    const json = JSON.stringify(
      { app: this.app, screens: [...this.screens.keys()], findings: this.findings },
      null,
      2,
    );
    writeFileSync(join(dir, `${this.app}.md`), markdown);
    writeFileSync(join(dir, `${this.app}.json`), json);
    await testInfo.attach(`a11y-${this.app}.md`, { body: markdown, contentType: 'text/markdown' });
    await testInfo.attach(`a11y-${this.app}.json`, { body: json, contentType: 'application/json' });
    console.log(markdown);
    return markdown;
  }
}

export interface ScreenOptions {
  /** Defaults to all eight variants. */
  variants?: Variant[];
  /** Selectors left out of the axe scan (e.g. third-party content); none by default. */
  exclude?: string[];
  /** Runs after each variant is applied (e.g. to reopen a dialog that a resize closed). */
  prepare?: (page: Page, variant: Variant) => Promise<void>;
  /** false skips the keyboard walk (e.g. a page with a modal dialog, which is walked separately). */
  keyboard?: boolean;
  /** Only stops inside this selector are judged; the walk still starts at the top. */
  keyboardScope?: string;
  /** false skips the reflow and text spacing checks. */
  reflow?: boolean;
}

export function variantLabel(v: Variant): string {
  return `${v.language}/${v.theme}/${v.width}`;
}

async function currentLanguage(page: Page): Promise<Language> {
  return (await page.locator('html').getAttribute('lang')) === 'en' ? 'en' : 'tr';
}

/** Applies a variant in place: the app's language switcher, the emulated colour scheme and the viewport. */
export async function applyVariant(page: Page, variant: Variant): Promise<void> {
  await page.emulateMedia({ colorScheme: variant.theme });
  await page.setViewportSize(viewports[variant.width]);
  if ((await currentLanguage(page)) !== variant.language) {
    const switcher = page.getByTestId('language-switcher').first();
    if (await switcher.isVisible()) {
      // A DOM click: an open drawer or dialog may cover the header button, and covering it is not the point here.
      await switcher.dispatchEvent('click');
    } else {
      // A page without the switcher (the payment pages a provider returns to): the stored preference and a reload,
      // as a returning visitor would see it (DEC-428). Only used on pages that need no in-memory session.
      await page.evaluate(
        (language) => localStorage.setItem('sa-language', language),
        variant.language,
      );
      await page.reload();
    }
    await expect(page.locator('html')).toHaveAttribute('lang', variant.language);
  }
  await expect(page.locator('html')).toHaveClass(new RegExp(`\\b${variant.theme}\\b`));
  await settle(page);
}

/** Waits until finite animations and transitions are over, so colours and positions are final. */
export async function settle(page: Page): Promise<void> {
  // Twice, a moment apart: a list that finishes reloading just after the first check re-enables its buttons, which
  // then fade in (P3-11: a half-faded button reads as low contrast).
  for (let check = 0; check < 2; check++) {
    if (check > 0) await page.waitForTimeout(250);
    await page.waitForFunction(() =>
      document
        .getAnimations()
        .every((a) => a.playState !== 'running' || a.effect?.getTiming().iterations === Infinity),
    );
  }
}

interface FocusProbe {
  id: string;
  description: string;
  inScope: boolean;
  indicator: boolean;
  /** A native date/time input: several internal stops, one of them the picker button Chromium draws itself. */
  native: boolean;
  obscured: 'no' | 'partly' | 'entirely';
  coveredBy: string;
}

/**
 * Runs in the page: describes the focused element, whether focusing it changed how it (or, for a visually hidden
 * input, its label) looks, and whether it is covered. The element is blurred to compare; Chromium keeps it as the
 * sequential focus navigation starting point, so the next Tab moves on from it.
 */
function probeFocus(scope: string | null): FocusProbe | null {
  const el = document.activeElement as HTMLElement | null;
  if (!el || el === document.body || el === document.documentElement) return null;
  const tiny = (node: Element) => {
    const r = node.getBoundingClientRect();
    return r.width <= 2 || r.height <= 2;
  };
  // A visually hidden input (custom radio or checkbox) shows its focus on its label.
  const shown: HTMLElement =
    tiny(el) && el instanceof HTMLInputElement && el.labels?.[0] ? el.labels[0] : el;
  const watch = [shown, ...Array.from(shown.querySelectorAll<HTMLElement>('*')).slice(0, 20)];
  if (shown.parentElement) watch.push(shown.parentElement);
  const look = () =>
    watch
      .map((node) => {
        const s = getComputedStyle(node);
        const outline =
          s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0
            ? `${s.outlineStyle} ${s.outlineWidth} ${s.outlineColor}`
            : 'none';
        return [
          outline,
          s.boxShadow,
          s.borderColor,
          s.backgroundColor,
          s.color,
          s.textDecorationLine,
        ].join(';');
      })
      .join('|');
  const name = (el.getAttribute('aria-label') ?? el.textContent ?? '')
    .trim()
    .replace(/\s+/g, ' ')
    .slice(0, 40);
  const description = `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ''}${name ? ` "${name}"` : ''}`;
  const path: string[] = [];
  for (let node: Element | null = el; node; node = node.parentElement) {
    path.unshift(`${node.tagName}:${Array.from(node.parentElement?.children ?? []).indexOf(node)}`);
  }

  // Covered: sample the centre and four inset points of the visible box.
  const rect = shown.getBoundingClientRect();
  const points = [
    [rect.left + rect.width / 2, rect.top + rect.height / 2],
    [rect.left + 2, rect.top + 2],
    [rect.right - 2, rect.top + 2],
    [rect.left + 2, rect.bottom - 2],
    [rect.right - 2, rect.bottom - 2],
  ];
  let covered = 0;
  let coveredBy = '';
  for (const [x, y] of points) {
    const outside = x < 0 || y < 0 || x > innerWidth || y > innerHeight;
    const hit = outside ? null : document.elementFromPoint(x, y);
    const own = hit && (shown.contains(hit) || hit.contains(shown) || el.contains(hit));
    if (!own) {
      covered++;
      if (hit && !coveredBy) {
        coveredBy = `${hit.tagName.toLowerCase()}${hit.id ? `#${hit.id}` : ''}.${String(hit.className).split(' ').slice(0, 3).join('.')}`;
      } else if (!hit && !coveredBy) {
        coveredBy = 'outside the viewport';
      }
    }
  }

  const focused = look();
  el.blur();
  const unfocused = look();
  return {
    id: path.join('/'),
    description,
    inScope: scope ? !!el.closest(scope) : true,
    indicator: focused !== unfocused,
    native:
      el instanceof HTMLInputElement &&
      ['date', 'month', 'week', 'time', 'datetime-local'].includes(el.type),
    obscured: covered === points.length ? 'entirely' : covered > 0 ? 'partly' : 'no',
    coveredBy,
  };
}

/** Elements that make the page (or the admin's scrolling main area) wider than the viewport. */
function horizontalOverflow(): string[] {
  const offenders: string[] = [];
  const root = document.scrollingElement ?? document.documentElement;
  if (root.scrollWidth > root.clientWidth + 1) {
    offenders.push(`document ${root.scrollWidth}px > ${root.clientWidth}px`);
    // The innermost elements reaching past the viewport (the page itself scrolls, so no box clips them).
    for (const node of Array.from(document.querySelectorAll<HTMLElement>('body *'))) {
      const r = node.getBoundingClientRect();
      if (r.width === 0 || r.right <= innerWidth + 1) continue;
      if (Array.from(node.children).some((c) => c.getBoundingClientRect().right > innerWidth + 1))
        continue;
      // Content inside a box that clips or scrolls cannot widen the page.
      let clipped = false;
      for (let p = node.parentElement; p && p !== document.body; p = p.parentElement) {
        if (getComputedStyle(p).overflowX !== 'visible') {
          clipped = true;
          break;
        }
      }
      if (clipped) continue;
      offenders.push(
        `${node.tagName.toLowerCase()}.${String(node.className).split(' ').slice(0, 3).join('.')} right=${Math.round(r.right)} ${getComputedStyle(node).position}`,
      );
      if (offenders.length > 6) return offenders;
    }
  }
  const main = document.querySelector('main');
  if (main && main.scrollWidth > main.clientWidth + 1) {
    offenders.push(`main ${main.scrollWidth}px > ${main.clientWidth}px`);
  }
  if (offenders.length === 0) return offenders;
  // Name the widest elements that are not inside their own horizontal scroll box.
  // The right edge of the content box that has to hold everything (inside the admin main area's padding).
  const width = main
    ? main.getBoundingClientRect().left +
      main.clientWidth -
      parseFloat(getComputedStyle(main).paddingRight)
    : root.clientWidth;
  for (const node of Array.from(document.querySelectorAll<HTMLElement>('body *'))) {
    const r = node.getBoundingClientRect();
    if (r.width === 0 || r.right <= width + 1) continue;
    let scrolled = false;
    for (let p = node.parentElement; p && p !== main && p !== document.body; p = p.parentElement) {
      const o = getComputedStyle(p).overflowX;
      if (o === 'auto' || o === 'scroll' || o === 'hidden' || o === 'clip') {
        scrolled = true;
        break;
      }
    }
    // Only the innermost offenders: a parent that is wide because of its child is not the cause.
    const wideChild = Array.from(node.children).some(
      (child) => child.getBoundingClientRect().right > width + 1,
    );
    if (!scrolled && !wideChild && getComputedStyle(node).position !== 'fixed') {
      offenders.push(
        `${node.tagName.toLowerCase()}.${String(node.className).split(' ').slice(0, 3).join('.')} right=${Math.round(r.right)}`,
      );
      if (offenders.length > 6) break;
    }
  }
  return offenders;
}

/** Visible text cut off by its own box (overflow hidden/clip) — ellipsis truncation with a full-text title is allowed. */
function clippedText(): string[] {
  const offenders: string[] = [];
  for (const node of Array.from(document.querySelectorAll<HTMLElement>('body *'))) {
    if (
      !Array.from(node.childNodes).some(
        (c) => c.nodeType === Node.TEXT_NODE && c.textContent!.trim(),
      )
    )
      continue;
    const s = getComputedStyle(node);
    const hidesX = s.overflowX === 'hidden' || s.overflowX === 'clip';
    const hidesY = s.overflowY === 'hidden' || s.overflowY === 'clip';
    if (!hidesX && !hidesY) continue;
    const r = node.getBoundingClientRect();
    // Visually hidden text (sr-only, clipped to nothing) is read by screen readers, not seen.
    if (
      r.width <= 2 ||
      r.height <= 2 ||
      s.clipPath === 'inset(50%)' ||
      s.clip === 'rect(0px, 0px, 0px, 0px)'
    )
      continue;
    const cutX =
      hidesX && node.scrollWidth > node.clientWidth + 1 && !node.title && !node.closest('[title]');
    const cutY = hidesY && node.scrollHeight > node.clientHeight + 1;
    if (cutX || cutY) {
      offenders.push(
        `${node.tagName.toLowerCase()}.${String(node.className).split(' ').slice(0, 3).join('.')} "${node.textContent!.trim().slice(0, 30)}"`,
      );
      if (offenders.length > 6) break;
    }
  }
  return offenders;
}
