import { getTabId } from '../offline/tabId'

/** The request never got an answer (offline, DNS, connection reset, ...). */
export class NetworkError extends Error {
  constructor(message = 'Network request failed') {
    super(message)
    this.name = 'NetworkError'
  }
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem | null

  constructor(status: number, problem: Problem | null, message?: string) {
    super(message ?? problem?.title ?? `Request failed with status ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** Field-level messages from a 400 validation response. */
  get fieldErrors(): Record<string, string[]> {
    return this.problem?.errors ?? {}
  }
}

/** A 409 from an optimistic-concurrency check: `current` is the server's latest state. */
export class ConflictError<T = unknown> extends ApiError {
  readonly current: T | undefined

  constructor(problem: Problem | null, current: T | undefined) {
    super(409, problem)
    this.name = 'ConflictError'
    this.current = current
  }
}

export interface Problem {
  title?: string
  detail?: string
  status?: number
  errors?: Record<string, string[]>
  current?: unknown
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  /** A multipart body (file upload). The browser sets the content type, including the boundary. */
  form?: FormData
  /** Sent as `If-Match`; required by the server for updates and deletes. */
  version?: string
  /** Sent as `Idempotency-Key`; retries with the same key are replayed, not re-run. */
  idempotencyKey?: string
  signal?: AbortSignal
}

export interface ApiResponse<T> {
  status: number
  data: T | null
}

let csrfToken: Promise<string> | null = null

async function fetchCsrfToken(): Promise<string> {
  const response = await fetch('/api/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })
  if (!response.ok) throw new ApiError(response.status, null)
  return ((await response.json()) as { token: string }).token
}

function csrf(): Promise<string> {
  csrfToken ??= fetchCsrfToken().catch((error: unknown) => {
    csrfToken = null
    throw error
  })
  return csrfToken
}

/** Tokens are bound to the signed-in identity, so forget them whenever it changes. */
export function resetCsrfToken(): void {
  csrfToken = null
}

async function send(
  path: string,
  options: RequestOptions,
  token: string | null,
): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json', 'X-Client-Id': getTabId() }
  if (options.body !== undefined && !options.form) headers['Content-Type'] = 'application/json'
  if (token) headers['X-CSRF-TOKEN'] = token
  if (options.version) headers['If-Match'] = `"${options.version}"`
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey

  try {
    return await fetch(path, {
      method: options.method ?? 'GET',
      credentials: 'same-origin',
      headers,
      body: options.form ?? (options.body === undefined ? undefined : JSON.stringify(options.body)),
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new NetworkError()
  }
}

async function readProblem(response: Response): Promise<Problem | null> {
  try {
    return (await response.json()) as Problem
  } catch {
    return null
  }
}

function isSafe(method: string): boolean {
  return method === 'GET'
}

export async function apiRequest<T>(
  path: string,
  options: RequestOptions = {},
): Promise<ApiResponse<T>> {
  const method = options.method ?? 'GET'
  let response: Response
  if (isSafe(method)) {
    response = await send(path, options, null)
  } else {
    response = await send(path, options, await csrf())
    if (response.status === 400 && (await response.clone().text()).includes('CSRF')) {
      resetCsrfToken()
      response = await send(path, options, await csrf())
    }
  }

  if (response.ok) {
    if (response.status === 204) return { status: 204, data: null }
    return { status: response.status, data: (await response.json()) as T }
  }

  const problem = await readProblem(response)
  if (response.status === 409 && problem && 'current' in problem) {
    throw new ConflictError(problem, problem.current)
  }
  throw new ApiError(response.status, problem)
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const { data } = await apiRequest<T>(path, { signal })
  return data as T
}
