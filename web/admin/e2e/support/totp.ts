import { createHmac } from 'node:crypto';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
const STEP_SECONDS = 30;

/** RFC 4648 Base32, case and spaces ignored: the setup key as the page and the API show it. */
export function base32Decode(text: string): Buffer {
  const bytes: number[] = [];
  let buffer = 0;
  let bits = 0;
  for (const char of text.replace(/[\s=-]/g, '').toUpperCase()) {
    const index = ALPHABET.indexOf(char);
    if (index < 0) {
      throw new Error('Not a Base32 character.');
    }
    buffer = (buffer << 5) | index;
    bits += 5;
    if (bits >= 8) {
      bytes.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
      buffer &= (1 << bits) - 1;
    }
  }
  return Buffer.from(bytes);
}

/**
 * The six-digit code of RFC 6238 (HMAC-SHA-1, 30 second steps) for the setup key, computed here the way an
 * authenticator app does (P5-01). `stepOffset` picks a neighbouring step: the server accepts one either side of its
 * own, and refuses a step it has already accepted, so a test that needs a second, unused code takes `+1`.
 */
export function totpCode(secret: string, stepOffset = 0, now = Date.now()): string {
  const step = Math.floor(now / 1000 / STEP_SECONDS) + stepOffset;
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const mac = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const offset = mac[mac.length - 1] & 0x0f;
  const binary = mac.readUInt32BE(offset) & 0x7fffffff;
  return String(binary % 1_000_000).padStart(6, '0');
}
