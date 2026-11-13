/** Hands a downloaded file to the browser without navigating away. */
export function saveFile(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Some browsers cancel a download whose URL is revoked at once; a short delay is enough.
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
