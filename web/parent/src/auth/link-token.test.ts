import { afterEach, describe, expect, it } from 'vitest';
import { forgetLinkToken, takeLinkToken } from './links';

describe('link token (P3-11, DEC-439)', () => {
  afterEach(() => {
    forgetLinkToken();
  });

  it('keeps the token after the fragment is gone, so a language switch does not lose the link', () => {
    expect(takeLinkToken('#token=abc')).toBe('abc');
    expect(takeLinkToken('')).toBe('abc');
  });

  it('prefers a newer link and forgets a used token', () => {
    takeLinkToken('#token=abc');
    expect(takeLinkToken('#token=def')).toBe('def');
    forgetLinkToken();
    expect(takeLinkToken('')).toBeNull();
  });
});
