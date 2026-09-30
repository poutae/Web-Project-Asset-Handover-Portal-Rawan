export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
    signal,
  })
  if (!response.ok) {
    throw new ApiError(response.status, `Request failed with status ${response.status}`)
  }
  return (await response.json()) as T
}
