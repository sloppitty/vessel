export type ApiErrorKind = 'unauthorized' | 'unavailable' | 'invalid-response' | 'http'

/** A failure the browser may show: `message` is always a fixed or bounded safe text, never a raw response body. */
export class ApiError extends Error {
  constructor(public readonly kind: ApiErrorKind, message: string, public readonly status?: number, public readonly retryAtUtc?: string,
    /** The ProblemDetails `code`, e.g. `step-up-required`; never shown as text. */
    public readonly code?: string) {
    super(message)
    this.name = 'ApiError'
  }
}

/** Show only API-safe text, retaining each caller's fallback for other failures. */
export function errorMessage(cause: unknown, fallback: string): string {
  if (cause instanceof ApiError && cause.kind === 'http' && cause.status === 429) return rateLimitMessage(cause.retryAtUtc)
  return cause instanceof ApiError ? cause.message : fallback
}

/** A wait notice, not a failed operation or an automatic retry. Never includes the response body. */
export function rateLimitMessage(retryAtUtc?: string | null): string {
  const seconds = Math.ceil((Date.parse(retryAtUtc ?? '') - Date.now()) / 1000)
  return Number.isFinite(seconds) && seconds > 0
    ? `Too many requests. Wait ${seconds} second${seconds === 1 ? '' : 's'} before trying again.`
    : 'Too many requests. Wait a moment before trying again.'
}
