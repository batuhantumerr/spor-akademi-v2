import { errorMessages, format } from '../i18n/tr';

/** An API failure reduced to the stable error code the UI translates (ADR-0007, appendix E). */
export class ApiError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
    readonly args: Readonly<Record<string, unknown>> = {},
    readonly traceId?: string,
  ) {
    super(code);
    this.name = 'ApiError';
  }
}

export const networkError = (): ApiError => new ApiError('Network.Unavailable', 0);

interface ProblemBody {
  code?: unknown;
  traceId?: unknown;
  args?: unknown;
}

/** The problem from an API response body (already parsed or not); never trusts anything but the code. */
export function problemFrom(body: unknown, status: number): ApiError {
  const problem = (typeof body === 'object' && body !== null ? body : {}) as ProblemBody;
  const code = typeof problem.code === 'string' ? problem.code : 'Unexpected.Error';
  const args =
    typeof problem.args === 'object' && problem.args !== null
      ? (problem.args as Record<string, unknown>)
      : {};
  const traceId = typeof problem.traceId === 'string' ? problem.traceId : undefined;
  return new ApiError(code, status, args, traceId);
}

export async function problemFromResponse(response: Response): Promise<ApiError> {
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    // Not a JSON problem (e.g. a proxy error page): only the status is known.
  }
  return problemFrom(body, response.status);
}

/** Any thrown value as an ApiError: fetch rejects with a TypeError when the network is down. */
export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (error instanceof TypeError) return networkError();
  return new ApiError('Unexpected.Error', 0);
}

/** Turkish, user-friendly message for an error; never shows internal details. */
export function errorMessage(error: unknown): string {
  const apiError = toApiError(error);
  const template = errorMessages[apiError.code] ?? errorMessages['Unexpected.Error'] ?? '';
  const args: Record<string, string | number> = {};
  for (const [key, value] of Object.entries(apiError.args)) {
    if (typeof value === 'string' || typeof value === 'number') args[key] = value;
  }
  return format(template, { ...args, traceId: apiError.traceId ?? '—' });
}

/**
 * Unwraps an openapi-fetch result: the data, or an ApiError with the problem's code. Network failures become
 * `Network.Unavailable`.
 */
export async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  let result: { data?: T; error?: unknown; response: Response };
  try {
    result = await call;
  } catch (error) {
    throw toApiError(error);
  }
  if (!result.response.ok) {
    throw problemFrom(result.error, result.response.status);
  }
  return result.data as T;
}
