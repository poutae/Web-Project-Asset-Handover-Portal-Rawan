import { lockNameFor } from './tabId'
import type { LockApi, NewOutboxEntry, OutboxEntry, OutboxStorage, SendResult } from './types'

export interface OutboxDeps {
  storage: OutboxStorage
  send: (entry: OutboxEntry) => Promise<SendResult>
  tabId: () => string
  userId: () => string
  /** Web Locks; when unavailable, dead tabs' entries are simply never adopted. */
  locks?: LockApi
  onChange?: () => void
  onSynced?: (entry: OutboxEntry) => void
}

export type ProcessOutcome = 'idle' | 'drained' | 'stopped-offline' | 'stopped-unauthorized'

/**
 * A durable, ordered queue of mutations kept in IndexedDB.
 *
 * - Every mutation is written here before it is sent, so it survives reloads, crashes and going offline.
 * - Entries are sent one at a time, strictly in the order they were made.
 * - Each entry carries its idempotency key, so re-sending after a lost response cannot apply it twice.
 * - Each tab owns only its own entries. Another tab (or window) sharing the same IndexedDB never sends
 *   them, which prevents two tabs from racing on one change. If a tab closes with entries left, a
 *   surviving tab adopts them once it can prove (via a Web Lock) that the owner is gone.
 * - A stale version yields a conflict for the user to resolve; nothing is ever overwritten silently.
 */
export class Outbox {
  private running: Promise<ProcessOutcome> | null = null
  private rerun = false

  private readonly deps: OutboxDeps

  constructor(deps: OutboxDeps) {
    this.deps = deps
  }

  async enqueue(input: NewOutboxEntry): Promise<OutboxEntry> {
    const entry = await this.deps.storage.add({
      ...input,
      id: crypto.randomUUID(),
      ownerTabId: this.deps.tabId(),
      userId: this.deps.userId(),
      createdAt: Date.now(),
      status: 'pending',
      attempts: 0,
    })
    this.deps.onChange?.()
    return entry
  }

  /** Entries this tab owns for the signed-in user, oldest first. */
  async list(): Promise<OutboxEntry[]> {
    const tabId = this.deps.tabId()
    const userId = this.deps.userId()
    return (await this.deps.storage.all()).filter(
      (e) => e.ownerTabId === tabId && e.userId === userId,
    )
  }

  /** Sends what can be sent. Calls made while a run is active schedule exactly one follow-up run. */
  process(): Promise<ProcessOutcome> {
    if (this.running) {
      this.rerun = true
      return this.running
    }
    this.running = this.runLoop().finally(() => {
      this.running = null
    })
    return this.running
  }

  async discard(seq: number): Promise<void> {
    await this.deps.storage.remove(seq)
    this.deps.onChange?.()
  }

  /**
   * Puts a failed entry back in the queue. It gets a fresh idempotency key: the server remembers the
   * outcome of the old key and would replay the same refusal instead of trying again.
   */
  async retry(seq: number): Promise<void> {
    await this.deps.storage.update(seq, {
      status: 'pending',
      error: undefined,
      id: crypto.randomUUID(),
    })
    this.deps.onChange?.()
  }

  /**
   * "Keep my change": re-queue a conflicted entry against the server's current version, so it now
   * deliberately replaces what the other person wrote. It is a different request from the one that
   * conflicted (new <c>If-Match</c>), so it gets a fresh idempotency key; reusing the old one would make
   * the server replay the stored 409.
   */
  async keepMine(entry: OutboxEntry): Promise<void> {
    const currentVersion = (entry.conflictCurrent as { version?: string } | undefined)?.version
    await this.deps.storage.update(entry.seq!, {
      status: 'pending',
      version: currentVersion ?? entry.version,
      conflictCurrent: undefined,
      error: undefined,
      id: crypto.randomUUID(),
    })
    this.deps.onChange?.()
  }

  /** Takes over entries of tabs that no longer exist. Returns how many entries were adopted. */
  async adoptOrphans(): Promise<number> {
    const locks = this.deps.locks
    if (!locks) return 0

    const me = this.deps.tabId()
    const userId = this.deps.userId()
    const owners = new Set(
      (await this.deps.storage.all())
        .filter((e) => e.userId === userId && e.ownerTabId !== me)
        .map((e) => e.ownerTabId),
    )

    let adopted = 0
    for (const owner of owners) {
      // The lock is only free if the owning tab is gone. Holding it while re-assigning stops two
      // survivors from adopting the same entries.
      adopted += await locks.request(lockNameFor(owner), { ifAvailable: true }, async (lock) =>
        lock ? this.deps.storage.reassign(owner, me, userId) : 0,
      )
    }
    if (adopted > 0) this.deps.onChange?.()
    return adopted
  }

  private async runLoop(): Promise<ProcessOutcome> {
    let outcome: ProcessOutcome = 'idle'
    do {
      this.rerun = false
      outcome = await this.drain()
    } while (this.rerun && outcome === 'drained')
    return outcome
  }

  private async drain(): Promise<ProcessOutcome> {
    await this.adoptOrphans()

    const entries = (await this.list()).filter((e) => e.status === 'pending')
    if (entries.length === 0) return 'idle'

    // When a resource has a conflicted or failed entry, later entries for it are stale; leave them queued.
    const blocked = new Set<string>()

    for (const [index, entry] of entries.entries()) {
      const key = entry.resourceKey
      if (key && blocked.has(key)) continue

      const result = await this.deps.send(entry)

      switch (result.kind) {
        case 'ok': {
          await this.deps.storage.remove(entry.seq!)
          if (key && result.version) {
            // The next queued change to this resource was made against the old version; hand it the
            // new one (and persist it, in case the run stops before reaching it).
            for (const later of entries.slice(index + 1)) {
              if (later.resourceKey !== key) continue
              later.version = result.version
              await this.deps.storage.update(later.seq!, { version: result.version })
            }
          }
          this.deps.onSynced?.(entry)
          break
        }

        case 'conflict':
          await this.deps.storage.update(entry.seq!, {
            status: 'conflict',
            conflictCurrent: result.current,
            attempts: entry.attempts + 1,
          })
          if (key) blocked.add(key)
          break

        case 'rejected':
          await this.deps.storage.update(entry.seq!, {
            status: 'failed',
            error: result.message,
            attempts: entry.attempts + 1,
          })
          if (key) blocked.add(key)
          break

        case 'retry':
          await this.deps.storage.update(entry.seq!, { attempts: entry.attempts + 1 })
          this.deps.onChange?.()
          return 'stopped-offline'

        case 'unauthorized':
          this.deps.onChange?.()
          return 'stopped-unauthorized'
      }
      this.deps.onChange?.()
    }
    return 'drained'
  }
}
