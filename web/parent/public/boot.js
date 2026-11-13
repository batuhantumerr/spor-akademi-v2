// Runs before first paint: index.html loads it synchronously in <head>. A file and not an inline script, so the
// Content-Security-Policy can say script-src 'self' (P4-01, DEC-502). nginx serves it with no-cache
// (docker/parent/nginx.conf), and the service worker precaches it with the rest of public/.

// Apply the stored or system theme before first paint to avoid a flash (same key as web/admin).
(function () {
  var pref = 'system';
  try {
    pref = localStorage.getItem('sa-theme') || 'system';
  } catch (e) {}
  var dark =
    pref === 'dark' || (pref === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.classList.add(dark ? 'dark' : 'light');
})();

// P3-10: the stored or browser language before first paint (same rule as src/i18n/language.ts).
(function () {
  var lang = null;
  try {
    lang = localStorage.getItem('sa-language');
  } catch (e) {}
  if (lang !== 'tr' && lang !== 'en') {
    var first = ((navigator.languages && navigator.languages[0]) || navigator.language || '').toLowerCase();
    lang = first === 'en' || first.indexOf('en-') === 0 ? 'en' : 'tr';
  }
  document.documentElement.lang = lang;
})();
