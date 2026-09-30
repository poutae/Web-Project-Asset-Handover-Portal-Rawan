import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, ConflictError, NetworkError, apiRequest, resetCsrfToken } from './http'

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('apiRequest', () => {
  let fetchMock: ReturnType<typeof vi.fn>

  beforeEach(() => {
    resetCsrfToken()
    fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => vi.unstubAllGlobals())

  it('does not send a CSRF token for reads', async () => {
    fetchMock.mockResolvedValueOnce(json({ ok: true }))
    await apiRequest('/api/projects')

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(fetchMock.mock.calls[0]?.[1].headers['X-CSRF-TOKEN']).toBeUndefined()
  })

  it('fetches a CSRF token once and sends it with mutations, plus If-Match and Idempotency-Key', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 'tok-1' }))
      .mockResolvedValueOnce(json({ id: '1' }, 201))
      .mockResolvedValueOnce(json({ id: '2' }, 201))

    await apiRequest('/api/projects/p/notes', {
      method: 'POST',
      body: { body: 'x' },
      idempotencyKey: 'key-1',
    })
    await apiRequest('/api/projects/p/notes/1', {
      method: 'PUT',
      body: {},
      version: 'AAA=',
      idempotencyKey: 'key-2',
    })

    expect(fetchMock.mock.calls.map((c) => c[0])).toEqual([
      '/api/auth/csrf',
      '/api/projects/p/notes',
      '/api/projects/p/notes/1',
    ])
    const post = fetchMock.mock.calls[1]?.[1]
    expect(post.headers['X-CSRF-TOKEN']).toBe('tok-1')
    expect(post.headers['Idempotency-Key']).toBe('key-1')
    const put = fetchMock.mock.calls[2]?.[1]
    expect(put.headers['If-Match']).toBe('"AAA="')
    expect(put.headers['X-CSRF-TOKEN']).toBe('tok-1')
  })

  it('refreshes the CSRF token once when the server rejects it', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 'stale' }))
      .mockResolvedValueOnce(json({ title: 'Invalid or missing CSRF token.' }, 400))
      .mockResolvedValueOnce(json({ token: 'fresh' }))
      .mockResolvedValueOnce(json({ ok: true }))

    const result = await apiRequest('/api/auth/logout', { method: 'POST' })

    expect(result.status).toBe(200)
    expect(fetchMock.mock.calls[3]?.[1].headers['X-CSRF-TOKEN']).toBe('fresh')
  })

  it('turns a 409 with the current state into a ConflictError', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 't' }))
      .mockResolvedValueOnce(
        json({ title: 'Modified', status: 409, current: { name: 'theirs', version: 'v2' } }, 409),
      )

    const error = await apiRequest('/api/projects/p', {
      method: 'PUT',
      body: {},
      version: 'v1',
    }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ConflictError)
    expect((error as ConflictError<{ name: string }>).current).toEqual({
      name: 'theirs',
      version: 'v2',
    })
  })

  it('reports validation errors by field', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 't' }))
      .mockResolvedValueOnce(
        json({ title: 'Invalid', errors: { name: ['name is required.'] } }, 400),
      )

    const error = await apiRequest('/api/projects', { method: 'POST', body: {} }).catch(
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).fieldErrors.name).toEqual(['name is required.'])
  })

  it('maps a failed fetch to NetworkError', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    await expect(apiRequest('/api/projects')).rejects.toBeInstanceOf(NetworkError)
  })

  it('returns no body for 204', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 't' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    expect(await apiRequest('/api/x', { method: 'DELETE', version: 'v' })).toEqual({
      status: 204,
      data: null,
    })
  })
})
