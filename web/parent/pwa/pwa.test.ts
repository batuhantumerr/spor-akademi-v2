import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { inflateSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import { drawIcon, encodePng, iconSpecs, parseHexColor, renderIcons } from './icons.ts';
import {
  injectPrecache,
  PRECACHE_PLACEHOLDER,
  precacheVersion,
  readThemeColors,
  shouldPrecache,
  webManifest,
} from './plugin.ts';

const tokens = readFileSync(resolve(import.meta.dirname, '../src/styles/tokens.css'), 'utf8');

describe('PWA build step (P3-07)', () => {
  it('takes the manifest colours from the light theme tokens', () => {
    expect(readThemeColors(tokens)).toEqual({ primary: '#1d4ed8', background: '#f8fafc' });
    expect(() => readThemeColors('.dark { --primary: #000000; }')).toThrow(/:root/);
    expect(() => readThemeColors(':root { --primary: oklch(0.5 0.2 260); --background: #fff; }')).toThrow(
      /#rrggbb/,
    );
  });

  it('writes an installable Turkish manifest with any and maskable icons in 192 and 512', () => {
    const colors = readThemeColors(tokens);
    const manifest = webManifest(colors, renderIcons(parseHexColor(colors.primary)));

    expect(manifest).toMatchObject({
      name: 'Spor Akademi Veli',
      short_name: 'Spor Akademi',
      lang: 'tr',
      start_url: '/',
      scope: '/',
      display: 'standalone',
      theme_color: '#1d4ed8',
      background_color: '#f8fafc',
    });
    const icons = manifest.icons as { src: string; sizes: string; purpose: string }[];
    expect(icons.map((icon) => `${icon.sizes} ${icon.purpose}`)).toEqual([
      '192x192 any',
      '512x512 any',
      '192x192 maskable',
      '512x512 maskable',
    ]);
    // Hashed under /assets/, so the immutable cache rule for build output is correct for them.
    for (const icon of icons) expect(icon.src).toMatch(/^\/assets\/icon-[\w-]+\.png$/);
  });

  it('draws valid PNG icons of the declared size', () => {
    for (const icon of renderIcons([29, 78, 216])) {
      const png = icon.png;
      expect([...png.subarray(0, 8)]).toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
      expect(png.toString('ascii', 12, 16)).toBe('IHDR');
      expect(png.readUInt32BE(16)).toBe(icon.size);
      expect(png.readUInt32BE(20)).toBe(icon.size);
    }
  });

  it('keeps the maskable ball inside the safe zone and makes apple and maskable icons opaque', () => {
    const primary = parseHexColor('#1d4ed8');
    for (const spec of iconSpecs) {
      const pixels = drawIcon(spec, primary);
      const at = (x: number, y: number) => [
        ...pixels.subarray((y * spec.size + x) * 4, (y * spec.size + x) * 4 + 4),
      ];
      const center = Math.floor(spec.size / 2);
      expect(at(center, center)).toEqual([255, 255, 255, 255]); // the ball
      if (spec.shape === 'any') {
        expect(at(0, 0)[3]).toBe(0); // rounded corner
      } else {
        expect(at(0, 0)).toEqual([...primary, 255]);
      }
      if (spec.shape === 'maskable') {
        // Nothing but background outside the 40 % radius safe zone.
        for (let x = 0; x < spec.size; x++) {
          const y = center;
          if (Math.abs(x + 0.5 - spec.size / 2) > spec.size * 0.4)
            expect(at(x, y)).toEqual([...primary, 255]);
        }
      }
    }
  });

  it('encodes the pixels it is given', () => {
    const rgba = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
    const png = encodePng(2, 1, rgba);
    const idatAt = png.indexOf('IDAT', 0, 'ascii');
    const length = png.readUInt32BE(idatAt - 4);
    const raw = inflateSync(png.subarray(idatAt + 4, idatAt + 4 + length));
    expect([...raw]).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8]);
    expect(() => encodePng(2, 2, rgba)).toThrow();
  });

  it('precaches the app shell, never the worker, source maps or rarely used font subsets', () => {
    expect(shouldPrecache('index.html')).toBe(true);
    expect(shouldPrecache('assets/index-Ab12Cd34.js')).toBe(true);
    expect(shouldPrecache('assets/index-Ab12Cd34.css')).toBe(true);
    expect(shouldPrecache('manifest.webmanifest')).toBe(true);
    expect(shouldPrecache('favicon.ico')).toBe(true);
    expect(shouldPrecache('assets/inter-latin-ext-wght-normal-Ab12.woff2')).toBe(true);
    expect(shouldPrecache('assets/inter-cyrillic-wght-normal-Ab12.woff2')).toBe(false);
    expect(shouldPrecache('sw.js')).toBe(false);
    expect(shouldPrecache('assets/index-Ab12Cd34.js.map')).toBe(false);
    expect(shouldPrecache('.vite/manifest.json')).toBe(false);
  });

  it('changes the version when any precached file changes', () => {
    const a = new Map<string, string>([
      ['index.html', '<html>1</html>'],
      ['assets/a.js', 'x'],
    ]);
    const b = new Map(a).set('index.html', '<html>2</html>');
    const reordered = new Map([...a].reverse());
    expect(precacheVersion(a)).toMatch(/^[0-9a-f]{16}$/);
    expect(precacheVersion(a)).not.toBe(precacheVersion(b));
    expect(precacheVersion(a)).toBe(precacheVersion(reordered));
  });

  it('injects the precache list exactly once', () => {
    const code = `var{version:o,files:s}=${PRECACHE_PLACEHOLDER};`;
    expect(injectPrecache(code, 'v1', ['/index.html'])).toBe(
      'var{version:o,files:s}={"version":"v1","files":["/index.html"]};',
    );
    expect(() => injectPrecache('nothing here', 'v1', [])).toThrow(/exactly once/);
    expect(() => injectPrecache(`${code}${code}`, 'v1', [])).toThrow(/exactly once/);
  });
});
