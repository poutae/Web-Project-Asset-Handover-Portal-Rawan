import { ApiError, ConflictError, NetworkError, apiRequest } from '../api/http'
import type { OutboxEntry, SendResult } from './types'

/** Performs one queued mutation and classifies the outcome for the outbox. */
export async function sendEntry(entry: OutboxEntry): Promise<SendResult> {
  try {
    const { data } = await apiRequest<{ version?: string }>(entry.url, {
      method: entry.method,
      body: entry.body,
      version: entry.version,
      idempotencyKey: entry.id,
    })
    return { kind: 'ok', version: data?.version }
  } catch (error) {
    if (error instanceof NetworkError) return { kind: 'retry' }
    if (error instanceof ConflictError) return { kind: 'conflict', current: error.current }
    if (error instanceof ApiError) {
      if (error.status === 401) return { kind: 'unauthorized' }
      // 409 without a body is "the same request is still running" (idempotency); the rest are transient.
      if (
        error.status === 409 ||
        error.status === 408 ||
        error.status === 429 ||
        error.status >= 500
      ) {
        return { kind: 'retry' }
      }
      return { kind: 'rejected', message: describe(error) }
    }
    throw error
  }
}

function describe(error: ApiError): string {
  const first = Object.values(error.fieldErrors)[0]?.[0]
  return (
    first ??
    error.problem?.title ??
    error.problem?.detail ??
    `The server refused this change (${error.status}).`
  )
}
