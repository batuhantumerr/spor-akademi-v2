import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import { session } from '../auth/session';
import { LANGUAGE_STORAGE_KEY, setLanguage } from '../i18n/language';

beforeEach(() => {
  // Each test starts signed out, as a page without a refresh cookie does; tests sign in when they need to.
  session.forget();
  // jsdom reports en-US as the browser language; tests assert the default Turkish UI unless they switch (P3-10).
  setLanguage('tr');
  localStorage.removeItem(LANGUAGE_STORAGE_KEY);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  document.documentElement.className = '';
  window.history.replaceState(null, '', '/');
});
