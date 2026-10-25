import { ApiError } from './errors'
import { isGuid, isRecord, isText } from './guards'

/**
 * How a call proves who it is. `session` rides the same-origin session cookie and binds the tab to its user with
 * `X-Vessel-User`. `public` is for the anonymous auth routes and the session probe. Both send a real `Origin` despite
 * the page's `no-referrer` policy.
 */
export type ApiCredentials = SessionCredentials | { kind: 'public' }
export type SessionCredentials = { kind: 'session'; userId: string; sessionId: string; generation?: SessionGeneration }

/**
 * The tab's session generation. It grows when this tab adopts a newer session of the same user from another tab;
 * answers to requests sent under an older generation are never published.
 */
export interface SessionGeneration {
  current(): number
  /** Hears every change until the returned function runs. */
  subscribe(listener: () => void): () => void
  /**
   * Aborts once the tab's session is over: a teardown, a sign-out, another person's sign-in or disposal. From then on
   * nothing is sent, retried, published or reopened; pending and later calls never settle.
   */
  readonly ended: AbortSignal
}

/**
 * What a session call returns once its session has ended. The page is always leaving then (teardown, sign-out and
 * another person's sign-in replace it; disposal without navigation unmounts the workspace), so no caller may report
 * a failure, toast or reload. Each call gets its own promise holding nothing, so it is collected with its caller.
 */
function afterSessionEnded(): Promise<never> {
  return new Promise<never>(() => {})
}

/** Copy for an answer discarded because the session changed meanwhile. A write may still have been applied. */
export const sessionChangedRead = 'Your session changed in another tab. Try again.'
export const sessionChangedWrite = 'Your session changed in another tab. This change may already be saved; check before trying again.'

/**
 * A 401 or 403 answer, with the ProblemDetails `code` when one was received. `generation` is the session
 * generation read when the request started, so a caller can discard answers to requests sent before a session change.
 */
export interface AuthFailure { status: 401 | 403; code?: string; generation?: number }

interface RequestOptions {
  method?: string
  body?: BodyInit
  signal?: AbortSignal
  accept?: string
  timeout: number
  unavailable?: string
  notFound?: string
  badGateway?: string
  tooMany?: string
  serviceUnavailable?: string
  safeError?: string
  noContent?: string
  invalidJson?: string
  invalidData?: string
  httpError?: (response: Response) => ApiError
  /** Copy for a ProblemDetails `code`; `undefined` keeps the status default. */
  codeMessage?: (code: string, status: number) => string | undefined
}

interface Problem { code?: string; detail?: string; title?: string }

const codePattern = /^[a-z][a-z-]{0,63}$/

async function readProblem(response: Response): Promise<Problem> {
  try {
    const problem: unknown = await response.json()
    if (!isRecord(problem)) return {}
    return {
      ...typeof problem.code === 'string' && codePattern.test(problem.code) ? { code: problem.code } : {},
      ...isText(problem.detail, 500) && problem.detail.trim() ? { detail: problem.detail } : {},
      ...isText(problem.title, 500) && problem.title.trim() ? { title: problem.title } : {},
    }
  } catch { return {} /* Keep the safe default for non-JSON proxy responses. */ }
}

function retryAt(response: Response) {
  const retryAfter = response.headers.get('Retry-After')
  const at = retryAfter === null ? undefined : /^\d+$/.test(retryAfter) ? Date.now() + Number(retryAfter) * 1000 : Date.parse(retryAfter)
  return at !== undefined && Number.isFinite(at) && Math.abs(at) <= 8.64e15 ? new Date(at).toISOString() : undefined
}

async function httpError(response: Response, options: RequestOptions): Promise<ApiError> {
  const auth = response.status === 401 || response.status === 403
  if (options.safeError) {
    // Only the code of a 401 or 403 is read, so session handling still works; the body is never shown.
    const code = auth ? (await readProblem(response)).code : undefined
    return new ApiError('http', options.safeError, response.status, retryAt(response), code)
  }
  if (options.httpError) return options.httpError(response)
  const problem = await readProblem(response)
  const coded = problem.code ? options.codeMessage?.(problem.code, response.status) : undefined
  if (auth) {
    return new ApiError(response.status === 401 || problem.code === 'user-suspended' ? 'unauthorized' : 'http',
      coded ?? (response.status === 401 ? 'Your session ended. Sign in again.' : 'Vessel refused this request.'),
      response.status, undefined, problem.code)
  }
  let detail = response.status === 502 && options.badGateway ? options.badGateway :
    response.status === 429 && options.tooMany ? options.tooMany : 'The request could not be completed.'
  // A caller's own 429 text wins; otherwise the API's bounded ProblemDetails explain the admission limit.
  if ([400, 409, 413, 415].includes(response.status) || response.status === 429 && !options.tooMany) {
    if (problem.detail) detail = problem.detail
    else if (response.status === 429 && problem.title) detail = problem.title
  }
  if (coded && !(response.status === 429 && options.tooMany)) detail = coded
  if (response.status === 404) detail = options.notFound ?? 'This account or portfolio is no longer available.'
  if (response.status === 503 && !coded) detail = options.serviceUnavailable ?? 'The service is unavailable. Check the database and server configuration.'
  const reference = response.headers.get('X-Correlation-ID')
  if (response.status >= 500 && reference && /^[\da-f]{32}$/.test(reference)) detail += ` Reference: ${reference}`
  return new ApiError('http', detail, response.status, retryAt(response), problem.code)
}

function transport(credentials: ApiCredentials): { headers: Record<string, string>; init: RequestInit } {
  switch (credentials.kind) {
    case 'session':
      if (!isGuid(credentials.userId)) throw new ApiError('unauthorized', 'Your session ended. Sign in again.')
      return { headers: { 'X-Vessel-User': credentials.userId }, init: { credentials: 'same-origin', referrerPolicy: 'same-origin' } }
    case 'public':
      return { headers: { 'X-Vessel-Request': '1' }, init: { credentials: 'same-origin', referrerPolicy: 'same-origin' } }
  }
}

type Outcome<T> = { ok: true; value: T } | { ok: false; error: unknown; failure?: Omit<AuthFailure, 'generation'> }

/** Frees what a discarded answer holds: a stream response's body. */
function discard(outcome: Outcome<unknown>) {
  if (outcome.ok && outcome.value instanceof Response) void outcome.value.body?.cancel().catch(() => undefined)
}

/**
 * One transport for JSON, images and stream headers. `onUnauthorized` hears every 401 and 403, with the answer's code;
 * the caller decides what ends a session.
 *
 * A session call checks its generation before it publishes anything. An answer to a request sent under an older
 * generation is dropped with its 401 or 403: a read (GET without a body) is sent once more under the current
 * generation, and anything else rejects with `session-changed` and calls `onSessionChanged`, since the server may
 * already have applied it. Once the session has ended, nothing more is sent or published: in-flight requests are
 * aborted, their bodies freed, and every call stays pending for good, without reporting failures or session changes.
 * A caller's own abort still rejects as before.
 */
export function createApiCall(credentials: ApiCredentials, onUnauthorized?: (failure: AuthFailure) => void, onSessionChanged?: () => void) {
  const { headers: identity, init } = transport(credentials)
  const source = credentials.kind === 'session' ? credentials.generation : undefined

  type Options<T> = RequestOptions & (
    { validate: (value: unknown) => value is T; read?: never } |
    { read: (response: Response) => Promise<T>; validate?: never }
  )

  async function send<T>(path: string, options: Options<T>): Promise<Outcome<T>> {
    // Clear the timer after decoding, or after headers for a long-lived SSE response.
    const timeout = new AbortController()
    const timer = setTimeout(() => timeout.abort(), options.timeout)
    const signals = [options.signal, timeout.signal, source?.ended].filter(signal => signal !== undefined)
    const signal = signals.length > 1 ? AbortSignal.any(signals) : timeout.signal
    let failure: Omit<AuthFailure, 'generation'> | undefined
    try {
      let response: Response
      try {
        response = await fetch(path, {
          method: options.method ?? 'GET', ...(options.body !== undefined ? { body: options.body } : {}),
          headers: { ...identity, Accept: options.accept ?? 'application/json',
            ...(typeof options.body === 'string' ? { 'Content-Type': 'application/json' } : {}) },
          ...init, redirect: 'error', cache: 'no-store', signal,
        })
      } catch {
        throw new ApiError('unavailable', options.safeError ?? options.unavailable ?? 'The request did not complete. Check the API and try again.')
      }
      if (!response.ok) {
        const error = await httpError(response, options)
        if (response.status === 401 || response.status === 403) failure = { status: response.status, ...error.code ? { code: error.code } : {} }
        throw error
      }
      if (options.read) return { ok: true, value: await options.read(response) }
      let body: unknown
      if (response.status === 204) {
        body = undefined
        if (!options.validate(body)) throw new ApiError('invalid-response', options.safeError ?? options.noContent ?? 'The API returned no workspace data.')
      } else {
        try { body = await response.json() } catch {
          throw new ApiError('invalid-response', options.safeError ?? options.invalidJson ?? 'The API returned invalid data.')
        }
        if (!options.validate(body)) throw new ApiError('invalid-response', options.safeError ?? options.invalidData ?? 'The API returned incompatible workspace data.')
      }
      return { ok: true, value: body }
    } catch (error) {
      return { ok: false, error, ...failure ? { failure } : {} }
    } finally {
      clearTimeout(timer)
    }
  }

  return async function call<T>(path: string, options: Options<T>): Promise<T> {
    const read = (options.method ?? 'GET') === 'GET' && options.body === undefined
    for (let attempt = 0; ; attempt++) {
      if (source?.ended.aborted) return afterSessionEnded()
      const generation = source?.current()
      const outcome = await send(path, options)
      if (source?.ended.aborted) {
        discard(outcome)
        return afterSessionEnded()
      }
      if (source && generation !== source.current()) {
        discard(outcome)
        if (read && attempt === 0 && !options.signal?.aborted) continue
        if (!read) onSessionChanged?.()
        throw new ApiError('http', read ? sessionChangedRead : sessionChangedWrite, undefined, undefined, 'session-changed')
      }
      if (outcome.ok) return outcome.value
      if (outcome.failure) {
        onUnauthorized?.({ ...outcome.failure, ...generation !== undefined ? { generation } : {} })
        // The 401 that ended the session belongs to the teardown too: its caller must not report a failure.
        if (source?.ended.aborted) return afterSessionEnded()
      }
      throw outcome.error
    }
  }
}
