import AxeBuilder from '@axe-core/playwright';
import { Page, TestInfo, expect } from '@playwright/test';

import { settle } from './wcag';

/**
 * NFR-USA-02: automated axe check of the page as it is now (WCAG 2.x A/AA rules). Only serious and
 * critical findings fail the test; minor and moderate ones are attached to the report for review.
 */
export async function expectNoSeriousA11yViolations(
  page: Page,
  testInfo: TestInfo,
  name: string,
): Promise<void> {
  // Colours are only meaningful once dialogs and buttons have finished their (finite) transitions.
  await settle(page);
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'])
    .analyze();
  const blocking = results.violations.filter(
    (v) => v.impact === 'serious' || v.impact === 'critical',
  );
  if (results.violations.length > 0) {
    await testInfo.attach(`axe-${name}.json`, {
      body: JSON.stringify(results.violations, null, 2),
      contentType: 'application/json',
    });
  }
  const summary = blocking.flatMap((v) =>
    v.nodes.map(
      (n) => `${v.impact} ${v.id}: ${n.target.join(' ')} ${n.html} — ${n.failureSummary ?? v.help}`,
    ),
  );
  expect(summary, `axe found serious/critical issues on ${name}`).toEqual([]);
}
