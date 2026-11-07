import { DocumentType } from '../../api/models';
import { LOCALE } from '../../core/i18n/locale';

/**
 * Client-side mirror of the API's document rules (DocumentRules, DEC-322). Only a convenience for a quick
 * message before uploading: the API inspects every file again and is the only authority.
 */
export const MAX_BYTES: Readonly<Record<DocumentType, number>> = {
  HealthReport: 5 * 1024 * 1024,
  Photo: 2 * 1024 * 1024,
};

export type DocumentFormat = 'Pdf' | 'Jpeg' | 'Png' | 'WebP';

export const FORMATS: Readonly<Record<DocumentType, readonly DocumentFormat[]>> = {
  HealthReport: ['Pdf', 'Jpeg', 'Png'],
  Photo: ['Jpeg', 'Png', 'WebP'],
};

const EXTENSIONS: Readonly<Record<DocumentFormat, readonly string[]>> = {
  Pdf: ['.pdf'],
  Jpeg: ['.jpg', '.jpeg'],
  Png: ['.png'],
  WebP: ['.webp'],
};

const MIME: Readonly<Record<DocumentFormat, string>> = {
  Pdf: 'application/pdf',
  Jpeg: 'image/jpeg',
  Png: 'image/png',
  WebP: 'image/webp',
};

/** The file input's <c>accept</c> list for a type. */
export function acceptFor(type: DocumentType): string {
  return FORMATS[type].flatMap((f) => [MIME[f], ...EXTENSIONS[f]]).join(',');
}

/** The format by magic bytes, like the API; <c>null</c> for anything else (SVG, HTML, GIF, ...). */
export function detectFormat(head: Uint8Array): DocumentFormat | null {
  const starts = (...bytes: number[]) => bytes.every((b, i) => head[i] === b);
  if (starts(0x25, 0x50, 0x44, 0x46, 0x2d)) {
    return 'Pdf';
  }
  if (starts(0xff, 0xd8, 0xff)) {
    return 'Jpeg';
  }
  if (starts(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)) {
    return 'Png';
  }
  const ascii = (from: number, to: number) => String.fromCharCode(...head.slice(from, to));
  if (head.length >= 12 && ascii(0, 4) === 'RIFF' && ascii(8, 12) === 'WEBP') {
    return 'WebP';
  }
  return null;
}

function extensionOf(name: string): string {
  const base = name.replace(/\\/g, '/').split('/').pop() ?? '';
  const dot = base.lastIndexOf('.');
  return dot > 0 ? base.slice(dot).toLowerCase() : '';
}

/**
 * The API error code the file would get, or <c>null</c> when it looks acceptable. Reads only the first bytes.
 */
export async function checkFile(type: DocumentType, file: File): Promise<string | null> {
  if (file.size === 0) {
    return 'Document.Empty';
  }
  if (file.size > MAX_BYTES[type]) {
    return 'Document.TooLarge';
  }
  const head = new Uint8Array(await file.slice(0, 16).arrayBuffer());
  const format = detectFormat(head);
  if (!format || !FORMATS[type].includes(format)) {
    return 'Document.UnsupportedType';
  }
  const extension = extensionOf(file.name);
  if (extension !== '' && !EXTENSIONS[format].includes(extension)) {
    return 'Document.ExtensionMismatch';
  }
  return null;
}

/** <c>1,5 MB</c>, <c>320 KB</c>, Turkish decimal comma. */
export function formatSize(bytes: number | string): string {
  const value = Number(bytes);
  const format = (n: number) =>
    new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 1 }).format(n);
  if (value >= 1024 * 1024) {
    return `${format(value / (1024 * 1024))} MB`;
  }
  return `${format(Math.max(1, Math.round(value / 1024)))} KB`;
}
