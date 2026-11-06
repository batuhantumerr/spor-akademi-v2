/**
 * Moves focus to the first invalid field after a rejected submit, so keyboard and screen-reader
 * users land on the error. Fields are given as [element id, invalid] in visual order.
 */
export function focusFirstInvalid(
  root: HTMLElement,
  fields: readonly (readonly [id: string, invalid: boolean])[],
): void {
  const first = fields.find(([, invalid]) => invalid);
  if (first) {
    root.querySelector<HTMLElement>(`#${first[0]}`)?.focus();
  }
}
