import { createHash } from 'node:crypto';
import { crc32, deflateSync } from 'node:zlib';

/**
 * The parent app's install icons (P3-07, DEC-394), drawn at build time from the theme colours: a white ball with
 * two seams on the primary colour. Original artwork, no third-party assets, no image library: a few shapes,
 * supersampled for smooth edges, written as RGBA PNG with Node's zlib.
 */

export type Rgb = readonly [number, number, number];

export interface IconSpec {
  /** Output file name stem, e.g. `icon-192`. */
  readonly name: string;
  readonly size: number;
  /**
   * `any`: a rounded square on a transparent canvas. `maskable`: full bleed, the ball inside the 80 % safe zone so
   * any mask shape keeps it whole. `apple`: full bleed and opaque (iOS rounds the corners itself).
   */
  readonly shape: 'any' | 'maskable' | 'apple';
}

export const iconSpecs: readonly IconSpec[] = [
  { name: 'icon-192', size: 192, shape: 'any' },
  { name: 'icon-512', size: 512, shape: 'any' },
  { name: 'icon-maskable-192', size: 192, shape: 'maskable' },
  { name: 'icon-maskable-512', size: 512, shape: 'maskable' },
  { name: 'apple-touch-icon', size: 180, shape: 'apple' },
];

export function parseHexColor(value: string): Rgb {
  const match = /^#([0-9a-f]{6})$/i.exec(value.trim());
  if (!match?.[1]) throw new Error(`Expected a #rrggbb colour, got "${value}".`);
  const hex = match[1];
  return [parseInt(hex.slice(0, 2), 16), parseInt(hex.slice(2, 4), 16), parseInt(hex.slice(4, 6), 16)];
}

const SAMPLES = 4; // 4 x 4 samples per pixel

/** RGBA pixels of one icon, row by row. */
export function drawIcon(spec: IconSpec, primary: Rgb): Uint8Array {
  const { size, shape } = spec;
  const pixels = new Uint8Array(size * size * 4);
  const center = size / 2;
  // The ball: radius relative to the icon; smaller for maskable so it stays inside the safe zone (radius 40 %).
  const ball = size * (shape === 'maskable' ? 0.27 : 0.31);
  const seamWidth = ball * 0.075;
  const seamRadius = ball * 0.9;
  const seamOffset = ball * 1.3;
  const corner = size * 0.22;

  const background = (x: number, y: number): boolean => {
    if (shape !== 'any') return true;
    // Rounded square filling the canvas.
    const dx = Math.max(Math.abs(x - center) - (center - corner), 0);
    const dy = Math.max(Math.abs(y - center) - (center - corner), 0);
    return dx * dx + dy * dy <= corner * corner;
  };
  const inBall = (x: number, y: number): boolean => (x - center) ** 2 + (y - center) ** 2 <= ball * ball;
  const onSeam = (x: number, y: number): boolean => {
    for (const side of [-1, 1]) {
      const d = Math.hypot(x - (center + side * seamOffset), y - center);
      if (Math.abs(d - seamRadius) <= seamWidth) return true;
    }
    return false;
  };

  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let covered = 0;
      let white = 0;
      for (let sy = 0; sy < SAMPLES; sy++) {
        for (let sx = 0; sx < SAMPLES; sx++) {
          const x = px + (sx + 0.5) / SAMPLES;
          const y = py + (sy + 0.5) / SAMPLES;
          if (!background(x, y)) continue;
          covered++;
          if (inBall(x, y) && !onSeam(x, y)) white++;
        }
      }
      const total = SAMPLES * SAMPLES;
      const offset = (py * size + px) * 4;
      if (covered === 0) continue; // transparent
      // Colour is the mix of primary and white within the covered part; alpha is the coverage.
      const w = white / covered;
      pixels[offset] = Math.round(primary[0] * (1 - w) + 255 * w);
      pixels[offset + 1] = Math.round(primary[1] * (1 - w) + 255 * w);
      pixels[offset + 2] = Math.round(primary[2] * (1 - w) + 255 * w);
      pixels[offset + 3] = Math.round((255 * covered) / total);
    }
  }
  return pixels;
}

/** A truecolour-with-alpha, 8-bit, non-interlaced PNG. */
export function encodePng(width: number, height: number, rgba: Uint8Array): Buffer {
  if (rgba.length !== width * height * 4) throw new Error('Pixel data does not match the size.');
  const rows = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    const start = y * (width * 4 + 1);
    rows[start] = 0; // filter: none
    rows.set(rgba.subarray(y * width * 4, (y + 1) * width * 4), start + 1);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8; // bit depth
  header[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const typeAndData = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(typeAndData), 0);
  return Buffer.concat([length, typeAndData, crc]);
}

export interface RenderedIcon extends IconSpec {
  /** Content-hashed path under `assets/`, so the immutable cache rule for build output applies. */
  readonly fileName: string;
  readonly png: Buffer;
}

export function renderIcons(primary: Rgb): RenderedIcon[] {
  return iconSpecs.map((spec) => {
    const png = encodePng(spec.size, spec.size, drawIcon(spec, primary));
    const hash = createHash('sha256').update(png).digest('base64url').slice(0, 8);
    return { ...spec, png, fileName: `assets/${spec.name}-${hash}.png` };
  });
}
