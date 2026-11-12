import { setLanguage } from './app/core/i18n/language';

// jsdom reports en-US as the browser language; specs assert the default Turkish UI unless they switch (P3-10).
beforeEach(() => setLanguage('tr'));
