import { HttpErrorResponse } from '@angular/common/http';

import { errorMessages, interpolate } from '../i18n/tr';

/** RFC 9457 problem body as returned by the API (ADR-0007). */
export interface ApiProblem {
  status?: number;
  title?: string;
  code: string;
  traceId?: string;
  args?: Record<string, unknown>;
  errors?: Record<string, string[]>;
}

/** Normalizes any HTTP failure into a problem with a code the UI can translate. */
export function toProblem(error: unknown): ApiProblem {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as Partial<ApiProblem> | null;
    if (body && typeof body === 'object' && typeof body.code === 'string') {
      return { ...body, code: body.code, status: error.status };
    }
    if (error.status === 0) {
      return { code: 'Network.Unavailable', status: 0 };
    }
    return { code: 'Unexpected.Error', status: error.status };
  }
  return { code: 'Unexpected.Error' };
}

/** Turkish, user-friendly message for a problem; never shows internal details. */
export function problemMessage(problem: ApiProblem): string {
  const template = errorMessages[problem.code] ?? errorMessages['Unexpected.Error'];
  return interpolate(template, { ...problem.args, traceId: problem.traceId ?? '—' });
}

/** Field errors keyed by camelCase path, translated. */
export function fieldErrors(problem: ApiProblem): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [field, codes] of Object.entries(problem.errors ?? {})) {
    const code = codes[0];
    result[field] = interpolate(
      errorMessages[code] ?? errorMessages['Field.Invalid'],
      problem.args,
    );
  }
  return result;
}
