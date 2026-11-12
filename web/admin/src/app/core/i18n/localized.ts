import { Language, language } from './language';

function isPlainObject(value: unknown): value is Record<PropertyKey, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function at(root: unknown, path: readonly PropertyKey[]): unknown {
  let node = root;
  for (const key of path) {
    node = isPlainObject(node) ? node[key] : undefined;
  }
  return node;
}

/**
 * One object that reads from the active language's dictionary on every property access (DEC-426).
 *
 * Components keep `protected readonly text = ui.groups;` and templates keep `{{ text.title }}`: nested objects
 * are views onto the same path, so a captured section follows the language too. Every read goes through the
 * `language` signal, which makes templates and `computed`s re-render on a switch without reloading the page.
 * A key missing in the active dictionary (only possible in the open `Record` maps) falls back to Turkish.
 * Arrays and leaf values are returned as they are.
 */
export function localized<T extends object>(
  dictionaries: Readonly<Record<Language, T>>,
  active: () => Language = language,
): T {
  const views = new Map<string, object>();

  const read = (path: readonly PropertyKey[]): Record<PropertyKey, unknown> => {
    const node = at(dictionaries[active()], path);
    return isPlainObject(node) ? node : (at(dictionaries.tr, path) as Record<PropertyKey, unknown>);
  };

  const valueAt = (path: readonly PropertyKey[], key: PropertyKey): unknown => {
    const value = read(path)[key];
    const resolved = value === undefined ? at(dictionaries.tr, [...path, key]) : value;
    return isPlainObject(resolved) ? view([...path, key]) : resolved;
  };

  const view = (path: readonly PropertyKey[]): object => {
    const id = path.map(String).join('\u0000');
    const existing = views.get(id);
    if (existing) return existing;
    // The Turkish node is the target, so the proxy has a stable shape (and a real object for invariants).
    // A section only some languages have (an open map) gets an empty stand-in target.
    const target = (at(dictionaries.tr, path) as object | undefined) ?? {};
    const proxy = new Proxy(target, {
      get: (_target, key) => valueAt(path, key),
      has: (_target, key) => key in read(path),
      ownKeys: () => Reflect.ownKeys(read(path)),
      getOwnPropertyDescriptor: (_target, key) =>
        Object.prototype.hasOwnProperty.call(read(path), key)
          ? { value: valueAt(path, key), writable: false, enumerable: true, configurable: true }
          : undefined,
      set: () => false,
      deleteProperty: () => false,
      defineProperty: () => false,
    });
    views.set(id, proxy);
    return proxy;
  };

  return view([]) as T;
}
