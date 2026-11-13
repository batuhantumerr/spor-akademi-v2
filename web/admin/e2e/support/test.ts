import { Browser, BrowserContext, test as base, expect } from '@playwright/test';

/**
 * P4-01 (DEC-502): the e2e servers send the images' Content-Security-Policy (e2e/serve.mjs), and every spec imports
 * `test` from here, so a CSP violation anywhere fails the test it happened in. Pages report violations through the
 * `securitypolicyviolation` event (all three engines); browser console messages about a blocked resource count too.
 * Contexts the specs open themselves (`browser.newContext()`, also in `beforeAll`) are watched as well; a
 * violation in a `beforeAll` fails the next test.
 */
const MARKER = '[csp-violation]';
const violations: string[] = [];
const watched = new WeakSet<BrowserContext>();

async function watch(context: BrowserContext): Promise<BrowserContext> {
  if (watched.has(context)) return context;
  watched.add(context);
  await context.addInitScript((marker) => {
    document.addEventListener('securitypolicyviolation', (event) => {
      console.error(
        `${marker} ${event.effectiveDirective} blocked ${event.blockedURI || '(inline)'} at ${event.sourceFile}:${event.lineNumber}`,
      );
    });
  }, MARKER);
  context.on('console', (message) => {
    const text = message.text();
    if (text.startsWith(MARKER) || /content[- ]security[- ]policy/i.test(text)) {
      violations.push(`${message.page()?.url() ?? '?'}: ${text}`);
    }
  });
  return context;
}

/** Violations so far, emptied; for a spec that provokes one on purpose. */
export function takeCspViolations(): string[] {
  return violations.splice(0);
}

const patched = new WeakSet<Browser>();

export const test = base.extend<{ cspGuard: void }, { cspBrowser: void }>({
  cspBrowser: [
    async ({ browser }, use) => {
      if (!patched.has(browser)) {
        patched.add(browser);
        const newContext = browser.newContext.bind(browser);
        browser.newContext = async (...args) => watch(await newContext(...args));
      }
      await use();
    },
    { scope: 'worker', auto: true },
  ],
  context: async ({ context }, use) => {
    await use(await watch(context));
  },
  cspGuard: [
    // Playwright reads a fixture's dependencies from the destructuring pattern; this one has none.
    // eslint-disable-next-line no-empty-pattern
    async ({}, use) => {
      await use();
      expect(takeCspViolations(), 'Content-Security-Policy violations').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
export type { Browser, BrowserContext, Locator, Page } from '@playwright/test';
