export type OutboxStatus = 'pending' | 'conflict' | 'failed'
export type ListName = 'projects' | 'milestones' | 'notes' | 'members'

/** How a queued change should look in the UI before the server has confirmed it. */
export type Optimistic =
  | { kind: 'create'; list: ListName; item: Record<string, unknown> }
  | { kind: 'update'; list: ListName; id: string; patch: Record<string, unknown> }
  | { kind: 'delete'; list: ListName; id: string }

export interface OutboxEntry {
  /** Storage key; assigned on insert. It defines the order in which changes are sent. */
  seq?: number
  /** Also used as the `Idempotency-Key`, so a retry after a lost response can never apply twice. */
  id: string
  /** The tab that may send this entry. Other tabs never touch it (see `adoptOrphans`). */
  ownerTabId: string
  userId: string
  method: 'POST' | 'PUT' | 'DELETE'
  url: string
  body?: unknown
  /** Sent as `If-Match`. */
  version?: string
  /** Entries for the same resource form a chain: each success passes its new version on to the next. */
  resourceKey?: string
  label: string
  optimistic?: Optimistic
  /** Query keys to refresh once the server has accepted the change. */
  invalidate: unknown[][]
  createdAt: number
  status: OutboxStatus
  attempts: number
  error?: string
  conflictCurrent?: unknown
}

export type NewOutboxEntry = Pick<
  OutboxEntry,
  'method' | 'url' | 'body' | 'version' | 'resourceKey' | 'label' | 'optimistic' | 'invalidate'
>

export type SendResult =
  | { kind: 'ok'; version?: string }
  | { kind: 'conflict'; current?: unknown }
  /** Offline, timeout, or the server is temporarily unavailable: keep the entry and try again later. */
  | { kind: 'retry' }
  | { kind: 'unauthorized' }
  /** The server understood and refused (validation, permission, gone): retrying will not help. */
  | { kind: 'rejected'; message: string }

export interface OutboxStorage {
  add(entry: Omit<OutboxEntry, 'seq'>): Promise<OutboxEntry>
  all(): Promise<OutboxEntry[]>
  update(seq: number, patch: Partial<OutboxEntry>): Promise<void>
  remove(seq: number): Promise<void>
  /** Re-assigns every entry of `userId` owned by `from` to `to`. */
  reassign(from: string, to: string, userId: string): Promise<number>
}

export interface LockApi {
  request<T>(
    name: string,
    options: { ifAvailable: boolean },
    callback: (lock: unknown) => Promise<T> | T,
  ): Promise<T>
}
