import { locale } from './language';

/** The UI locale for dates, numbers, sorting and case mapping (NFR-CMP-03): follows the active language. */
export { locale };

/**
 * A lazily created Intl formatter (or anything locale-bound) per locale. Calling the getter reads the language
 * signal, so a template or `computed` that formats a value re-renders when the language changes.
 */
export function perLocale<T>(create: (locale: string) => T): () => T {
  const cache = new Map<string, T>();
  return () => {
    const current = locale();
    let value = cache.get(current);
    if (value === undefined) {
      value = create(current);
      cache.set(current, value);
    }
    return value;
  };
}
