import { Browser, BrowserContext, test as base, expect } from '@playwright/test';

/**
 * P4-01 (DEC-502): the e2e servers send the images' Content-Security-Policy (e2e/serve.mjs), and every spec imports
 * `test` from here, so a CSP violation anywhere fails the test it happened in. Each page reports its
 * `securitypolicyviolation` events (all three engines) straight to the test process through a binding, so unrelated
 * console text cannot count and nothing depends on console timing. Before a test ends, one round trip to every open
 * page delivers reports still in flight. Contexts the specs open themselves (`browser.newContext()`, also in
 * `beforeAll`) are watched as well; a violation in a `beforeAll` fails the next test.
 */
const BINDING = '__reportCspViolation';
const violations: string[] = [];
const contexts = new Set<BrowserContext>();

async function watch(context: BrowserContext): Promise<BrowserContext> {
  if (contexts.has(context)) return context;
  contexts.add(context);
  context.on('close', () => contexts.delete(context));
  await context.exposeBinding(BINDING, ({ page }, report: string) => {
    violations.push(`${page.url()}: ${report}`);
  });
  await context.addInitScript((binding) => {
    document.addEventListener('securitypolicyviolation', (event) => {
      const report = (window as unknown as Record<string, (text: string) => Promise<void>>)[
        binding
      ];
      void report?.(
        `${event.effectiveDirective} blocked ${event.blockedURI || '(inline)'} at ${event.sourceFile}:${event.lineNumber}`,
      );
    });
  }, BINDING);
  return context;
}

/** Delivers reports the pages have sent but the test process has not handled yet. */
async function flush(): Promise<void> {
  for (const context of contexts) {
    for (const page of context.pages()) {
      await page.evaluate(() => 0).catch(() => undefined); // a closed or navigating page has nothing left to send
    }
  }
}

/** Violations so far, emptied; for a spec that provokes one on purpose. */
export async function takeCspViolations(): Promise<string[]> {
  await flush();
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
    // Depends on the test's context so that it ends first, while the pages can still deliver their reports.
    async ({ context }, use) => {
      void context;
      await use();
      expect(await takeCspViolations(), 'Content-Security-Policy violations').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
export type { Browser, BrowserContext, Locator, Page } from '@playwright/test';
