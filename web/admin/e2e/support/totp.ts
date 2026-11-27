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

/**
 * Hands out codes for one setup key, each for a time step later than the one before (V1-03). The server accepts a step
 * only once and never an older one than the last, and only within one step either side of its own clock, so a test that
 * needs several codes in a row takes the earliest usable step and, when that lies more than one step ahead, waits for the
 * clock to come within reach. A step is 30 seconds: three codes in a row take at most about a minute.
 */
export class TotpSequence {
  private last = Number.NEGATIVE_INFINITY;

  constructor(
    private readonly secret: string,
    /** Set when other code already used steps up to this one (unix seconds / 30). */
    spent?: number,
  ) {
    if (spent !== undefined) this.last = spent;
  }

  async next(): Promise<string> {
    const now = () => Math.floor(Date.now() / 1000 / STEP_SECONDS);
    const step = Math.max(this.last + 1, now() - 1);
    while (step > now() + 1) {
      const dueAt = (step - 1) * STEP_SECONDS * 1000;
      await new Promise((resolve) => setTimeout(resolve, Math.max(250, dueAt - Date.now() + 250)));
    }
    this.last = step;
    const at = Date.now();
    return totpCode(this.secret, step - Math.floor(at / 1000 / STEP_SECONDS), at);
  }
}
