import { afterEach, describe, expect, it, vi } from 'vitest'
import * as http from '../api/http'
import { sendEntry } from './send'
import type { OutboxEntry } from './types'

const entry: OutboxEntry = {
  id: 'key-1',
  ownerTabId: 'tab',
  userId: 'u',
  method: 'PUT',
  url: '/api/x/1',
  body: { a: 1 },
  version: 'v1',
  label: 'Edit',
  invalidate: [],
  createdAt: 0,
  status: 'pending',
  attempts: 0,
}

describe('sendEntry', () => {
  afterEach(() => vi.restoreAllMocks())

  it('sends the entry id as the idempotency key and returns the new version', async () => {
    const spy = vi
      .spyOn(http, 'apiRequest')
      .mockResolvedValue({ status: 200, data: { version: 'v2' } })

    expect(await sendEntry(entry)).toEqual({ kind: 'ok', version: 'v2' })
    expect(spy).toHaveBeenCalledWith('/api/x/1', {
      method: 'PUT',
      body: { a: 1 },
      version: 'v1',
      idempotencyKey: 'key-1',
    })
  })

  it.each([
    ['network failure', new http.NetworkError(), { kind: 'retry' }],
    [
      'conflict',
      new http.ConflictError(null, { version: 'v9' }),
      { kind: 'conflict', current: { version: 'v9' } },
    ],
    ['expired session', new http.ApiError(401, null), { kind: 'unauthorized' }],
    ['in-progress duplicate', new http.ApiError(409, null), { kind: 'retry' }],
    ['throttling', new http.ApiError(429, null), { kind: 'retry' }],
    ['server error', new http.ApiError(503, null), { kind: 'retry' }],
  ])('classifies %s', async (_name, error, expected) => {
    vi.spyOn(http, 'apiRequest').mockRejectedValue(error)

    expect(await sendEntry(entry)).toEqual(expected)
  })

  it('treats validation and permission failures as final', async () => {
    vi.spyOn(http, 'apiRequest').mockRejectedValue(
      new http.ApiError(400, { title: 'Invalid', errors: { title: ['title is required.'] } }),
    )

    expect(await sendEntry(entry)).toEqual({ kind: 'rejected', message: 'title is required.' })
  })

  it('treats 403 and 404 as final', async () => {
    vi.spyOn(http, 'apiRequest').mockRejectedValue(
      new http.ApiError(404, { title: 'The resource no longer exists.' }),
    )

    expect(await sendEntry(entry)).toEqual({
      kind: 'rejected',
      message: 'The resource no longer exists.',
    })
  })
})
