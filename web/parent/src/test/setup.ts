import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import { session } from '../auth/session';

beforeEach(() => {
  // Each test starts signed out, as a page without a refresh cookie does; tests sign in when they need to.
  session.forget();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  document.documentElement.className = '';
  window.history.replaceState(null, '', '/');
});
