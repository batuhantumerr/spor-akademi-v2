import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import type { Plugin, ResolvedConfig } from 'vite';
import { parseHexColor, renderIcons, type RenderedIcon } from './icons.ts';

/**
 * PWA build step of the parent app (P3-07, DEC-394), written for this app instead of a plugin package: it emits
 * the web app manifest, the icons and the service worker (sw/service-worker.ts → /sw.js) and injects the list of
 * files the worker precaches. No runtime dependency is added; the worker is a few hundred bytes of our own code.
 */

export const MANIFEST_FILE = 'manifest.webmanifest';
export const SW_FILE = 'sw.js';
/** Replaced in the built worker with `{ version, files }` (sw/service-worker.ts). */
export const PRECACHE_PLACEHOLDER = '__SA_PRECACHE__';

export interface ThemeColors {
  readonly primary: string;
  readonly background: string;
}

/** `--primary` and `--background` of the light theme (`:root`) from the shared token block (DEC-378). */
export function readThemeColors(tokensCss: string): ThemeColors {
  const root = /:root\s*\{([^}]*)\}/.exec(tokensCss)?.[1];
  if (!root) throw new Error('No :root block in the theme tokens.');
  const token = (name: string): string => {
    const value = new RegExp(`--${name}:\\s*([^;]+);`).exec(root)?.[1]?.trim();
    if (!value) throw new Error(`No --${name} in the theme tokens.`);
    parseHexColor(value); // the manifest needs a plain colour every browser reads
    return value;
  };
  return { primary: token('primary'), background: token('background') };
}

export function webManifest(colors: ThemeColors, icons: readonly RenderedIcon[]): Record<string, unknown> {
  return {
    id: '/',
    name: 'Spor Akademi Veli',
    short_name: 'Spor Akademi',
    description: 'Çocuklarınızın antrenman programı, yoklaması, aidatları ve okul duyuruları.',
    lang: 'tr',
    dir: 'ltr',
    start_url: '/',
    scope: '/',
    display: 'standalone',
    theme_color: colors.primary,
    background_color: colors.background,
    icons: icons
      .filter((icon) => icon.shape !== 'apple')
      .map((icon) => ({
        src: `/${icon.fileName}`,
        sizes: `${String(icon.size)}x${String(icon.size)}`,
        type: 'image/png',
        purpose: icon.shape === 'maskable' ? 'maskable' : 'any',
      })),
  };
}

/**
 * Which build files the worker precaches: the app shell (index.html, scripts, styles, icons, manifest, public
 * files) and the Latin font subsets Turkish text needs. Other font subsets are cached on first use. Never the
 * worker itself or source maps.
 */
export function shouldPrecache(fileName: string): boolean {
  const name = fileName.replaceAll('\\', '/');
  if (name === SW_FILE || name.endsWith('.map') || name.startsWith('.vite/')) return false;
  if (name.endsWith('.woff2') || name.endsWith('.woff')) return name.includes('latin');
  return true;
}

/** A version that changes whenever any precached file's content changes. */
export function precacheVersion(files: ReadonlyMap<string, string | Uint8Array>): string {
  const hash = createHash('sha256');
  const entries = [...files].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  for (const [name, content] of entries) {
    hash.update(name).update('\0').update(createHash('sha256').update(content).digest()).update('\0');
  }
  return hash.digest('hex').slice(0, 16);
}

/** The built worker with the precache list in place of the placeholder; fails the build if it is not there once. */
export function injectPrecache(code: string, version: string, files: readonly string[]): string {
  const parts = code.split(PRECACHE_PLACEHOLDER);
  if (parts.length !== 2) {
    throw new Error(
      `${SW_FILE}: expected ${PRECACHE_PLACEHOLDER} exactly once, found ${String(parts.length - 1)}.`,
    );
  }
  return parts.join(JSON.stringify({ version, files }));
}

/** Files under `dir` (recursively) as absolute paths; none when it does not exist. */
function listFiles(dir: string): string[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name));
}

export function parentPwa(): Plugin {
  let config: ResolvedConfig;
  let colors: ThemeColors;
  let icons: RenderedIcon[];
  let manifestJson: string;
  let swRef: string | undefined;

  return {
    name: 'spor-akademi-parent-pwa',
    enforce: 'post',
    configResolved(resolved) {
      config = resolved;
      colors = readThemeColors(readFileSync(resolve(config.root, 'src/styles/tokens.css'), 'utf8'));
      icons = renderIcons(parseHexColor(colors.primary));
      manifestJson = `${JSON.stringify(webManifest(colors, icons), null, 2)}\n`;
    },
    transformIndexHtml() {
      const apple = icons.find((icon) => icon.shape === 'apple');
      if (!apple) throw new Error('No apple-touch-icon.');
      return [
        { tag: 'link', attrs: { rel: 'manifest', href: `/${MANIFEST_FILE}` }, injectTo: 'head' },
        { tag: 'meta', attrs: { name: 'theme-color', content: colors.primary }, injectTo: 'head' },
        { tag: 'link', attrs: { rel: 'apple-touch-icon', href: `/${apple.fileName}` }, injectTo: 'head' },
      ];
    },
    // `npm run dev` serves the manifest and icons too (the worker is registered in production builds only).
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        const path = (req.url ?? '').split('?')[0];
        if (path === `/${MANIFEST_FILE}`) {
          res.setHeader('Content-Type', 'application/manifest+json');
          res.end(manifestJson);
          return;
        }
        const icon = icons.find((candidate) => path === `/${candidate.fileName}`);
        if (icon) {
          res.setHeader('Content-Type', 'image/png');
          res.end(icon.png);
          return;
        }
        next();
      });
    },
    buildStart() {
      if (config.command !== 'build') return;
      swRef = this.emitFile({
        type: 'chunk',
        id: resolve(config.root, 'sw/service-worker.ts'),
        fileName: SW_FILE,
      });
    },
    generateBundle(_options, bundle) {
      if (config.command !== 'build' || !swRef) return;
      for (const icon of icons) {
        this.emitFile({ type: 'asset', fileName: icon.fileName, source: icon.png });
      }
      this.emitFile({ type: 'asset', fileName: MANIFEST_FILE, source: manifestJson });

      const contents = new Map<string, string | Uint8Array>();
      for (const [fileName, output] of Object.entries(bundle)) {
        if (!shouldPrecache(fileName)) continue;
        contents.set(fileName, output.type === 'chunk' ? output.code : output.source);
      }
      // Emitted above; listed explicitly in case the bundle object does not show them yet.
      for (const icon of icons) contents.set(icon.fileName, icon.png);
      contents.set(MANIFEST_FILE, manifestJson);
      // Files copied from public/ (favicon.ico) are not part of the bundle.
      if (config.publicDir) {
        for (const file of listFiles(config.publicDir)) {
          const name = relative(config.publicDir, file).split(sep).join('/');
          if (shouldPrecache(name)) contents.set(name, readFileSync(file));
        }
      }
      if (!contents.has('index.html')) throw new Error('index.html is not in the bundle.');

      const sw = Object.values(bundle).find((output) => output.fileName === SW_FILE);
      if (sw?.type !== 'chunk') throw new Error(`${SW_FILE} is not in the bundle.`);
      // A classic worker script cannot import: everything it needs must be inlined.
      if (sw.imports.length > 0 || sw.dynamicImports.length > 0) {
        throw new Error(
          `${SW_FILE} must not import other chunks (found ${[...sw.imports, ...sw.dynamicImports].join(', ')}).`,
        );
      }
      const files = [...contents.keys()].sort().map((name) => `/${name}`);
      sw.code = injectPrecache(sw.code, precacheVersion(contents), files);
    },
  };
}
