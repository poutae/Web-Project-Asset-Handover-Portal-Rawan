import 'fake-indexeddb/auto'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { Outbox } from './outbox'
import { DB_NAME, indexedDbOutboxStorage, resetDbConnection } from './storage'
import { lockNameFor } from './tabId'
import type { LockApi, NewOutboxEntry, OutboxEntry, SendResult } from './types'

const note = (text: string, extra: Partial<NewOutboxEntry> = {}): NewOutboxEntry => ({
  method: 'POST',
  url: '/api/projects/p1/notes',
  body: { body: text },
  label: `Add note "${text}"`,
  invalidate: [['notes', 'p1']],
  ...extra,
})

/** A stand-in for navigator.locks: a lock is "held" while its tab is alive. */
class FakeLocks implements LockApi {
  held = new Set<string>()
  async request<T>(
    name: string,
    _o: { ifAvailable: boolean },
    cb: (lock: unknown) => Promise<T> | T,
  ): Promise<T> {
    if (this.held.has(name)) return cb(null)
    this.held.add(name)
    try {
      return await cb({ name })
    } finally {
      this.held.delete(name)
    }
  }
}

function makeOutbox(opts: {
  tab: string
  user?: string
  send: (e: OutboxEntry) => Promise<SendResult>
  locks?: LockApi
  onSynced?: (e: OutboxEntry) => void
}) {
  return new Outbox({
    storage: indexedDbOutboxStorage,
    send: opts.send,
    tabId: () => opts.tab,
    userId: () => opts.user ?? 'user-1',
    locks: opts.locks,
    onSynced: opts.onSynced,
  })
}

beforeEach(async () => {
  resetDbConnection()
  await new Promise<void>((resolve, reject) => {
    const request = indexedDB.deleteDatabase(DB_NAME)
    request.onsuccess = () => resolve()
    request.onerror = () => reject(request.error)
  })
})

describe('Outbox', () => {
  it('persists entries and sends them strictly in the order they were made', async () => {
    const sent: string[] = []
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        sent.push((e.body as { body: string }).body)
        return { kind: 'ok' }
      },
    })

    await outbox.enqueue(note('one'))
    await outbox.enqueue(note('two'))
    await outbox.enqueue(note('three'))

    // A "reload": a brand-new Outbox over the same IndexedDB still sees all three, in order.
    const afterReload = makeOutbox({ tab: 'A', send: async () => ({ kind: 'retry' }) })
    expect((await afterReload.list()).map((e) => (e.body as { body: string }).body)).toEqual([
      'one',
      'two',
      'three',
    ])

    expect(await outbox.process()).toBe('drained')
    expect(sent).toEqual(['one', 'two', 'three'])
    expect(await outbox.list()).toEqual([])
  })

  it('uses each entry id as its idempotency key and reuses it on retry', async () => {
    const seen: string[] = []
    let online = false
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        seen.push(e.id)
        return online ? { kind: 'ok' } : { kind: 'retry' }
      },
    })
    await outbox.enqueue(note('once'))

    expect(await outbox.process()).toBe('stopped-offline')
    online = true
    expect(await outbox.process()).toBe('drained')

    expect(seen).toHaveLength(2)
    expect(seen[0]).toBe(seen[1])
  })

  it('stops at the first offline failure and keeps the order when it resumes', async () => {
    const sent: string[] = []
    let online = false
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        if (!online) return { kind: 'retry' }
        sent.push((e.body as { body: string }).body)
        return { kind: 'ok' }
      },
    })
    await outbox.enqueue(note('one'))
    await outbox.enqueue(note('two'))

    expect(await outbox.process()).toBe('stopped-offline')
    expect(sent).toEqual([])
    expect(await outbox.list()).toHaveLength(2)

    online = true
    await outbox.process()
    expect(sent).toEqual(['one', 'two'])
  })

  it('passes each new version down the chain of edits to the same resource', async () => {
    const versionsSent: (string | undefined)[] = []
    let next = 1
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        versionsSent.push(e.version)
        return { kind: 'ok', version: `v${++next}` }
      },
    })
    const edit = (title: string): NewOutboxEntry => ({
      method: 'PUT',
      url: '/api/projects/p1/milestones/m1',
      body: { title },
      version: 'v1',
      resourceKey: 'milestone:m1',
      label: `Edit ${title}`,
      invalidate: [],
    })
    await outbox.enqueue(edit('a'))
    await outbox.enqueue(edit('b'))
    await outbox.enqueue(edit('c'))

    await outbox.process()

    expect(versionsSent).toEqual(['v1', 'v2', 'v3'])
  })

  it('records a conflict, keeps going for other resources, and holds back stale follow-ups', async () => {
    const sent: string[] = []
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        sent.push(e.label)
        return e.label === 'edit-1'
          ? { kind: 'conflict', current: { version: 'server-v', title: 'theirs' } }
          : { kind: 'ok' }
      },
    })
    const put = (label: string, resourceKey: string): NewOutboxEntry => ({
      method: 'PUT',
      url: `/api/x/${resourceKey}`,
      body: {},
      version: 'old',
      resourceKey,
      label,
      invalidate: [],
    })
    await outbox.enqueue(put('edit-1', 'm1'))
    await outbox.enqueue(put('edit-1-again', 'm1'))
    await outbox.enqueue(put('edit-2', 'm2'))

    await outbox.process()

    expect(sent).toEqual(['edit-1', 'edit-2'])
    const remaining = await outbox.list()
    expect(remaining.map((e) => [e.label, e.status])).toEqual([
      ['edit-1', 'conflict'],
      ['edit-1-again', 'pending'],
    ])
    expect(remaining[0]?.conflictCurrent).toEqual({ version: 'server-v', title: 'theirs' })
  })

  it('"keep mine" re-queues a conflicted entry against the server version', async () => {
    let conflict = true
    const versions: (string | undefined)[] = []
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        versions.push(e.version)
        return conflict ? { kind: 'conflict', current: { version: 'v9' } } : { kind: 'ok' }
      },
    })
    await outbox.enqueue({
      method: 'PUT',
      url: '/x',
      body: {},
      version: 'v1',
      resourceKey: 'r',
      label: 'edit',
      invalidate: [],
    })
    await outbox.process()

    const [conflicted] = await outbox.list()
    conflict = false
    await outbox.keepMine(conflicted!)
    await outbox.process()

    expect(versions).toEqual(['v1', 'v9'])
    expect(await outbox.list()).toEqual([])
  })

  it('gives a re-queued entry a new idempotency key, so the server does not replay the old outcome', async () => {
    const ids: string[] = []
    let first = true
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        ids.push(e.id)
        const result: SendResult = first
          ? { kind: 'conflict', current: { version: 'v2' } }
          : { kind: 'ok' }
        first = false
        return result
      },
    })
    await outbox.enqueue({
      method: 'PUT',
      url: '/x',
      body: {},
      version: 'v1',
      resourceKey: 'r',
      label: 'edit',
      invalidate: [],
    })
    await outbox.process()
    const [conflicted] = await outbox.list()

    await outbox.keepMine(conflicted!)
    await outbox.process()

    expect(ids).toHaveLength(2)
    expect(ids[1]).not.toBe(ids[0])
  })

  it('gives a retried failed entry a new idempotency key too', async () => {
    const ids: string[] = []
    let refuse = true
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) => {
        ids.push(e.id)
        return refuse ? { kind: 'rejected', message: 'nope' } : { kind: 'ok' }
      },
    })
    await outbox.enqueue(note('x'))
    await outbox.process()
    const [failed] = await outbox.list()

    refuse = false
    await outbox.retry(failed!.seq!)
    await outbox.process()

    expect(ids[1]).not.toBe(ids[0])
    expect(await outbox.list()).toEqual([])
  })

  it('marks refused changes as failed without blocking unrelated ones', async () => {
    const outbox = makeOutbox({
      tab: 'A',
      send: async (e) =>
        e.label === 'bad' ? { kind: 'rejected', message: 'nope' } : { kind: 'ok' },
    })
    await outbox.enqueue(note('x', { label: 'bad' }))
    await outbox.enqueue(note('y', { label: 'good' }))

    expect(await outbox.process()).toBe('drained')

    const left = await outbox.list()
    expect(left.map((e) => [e.label, e.status, e.error])).toEqual([['bad', 'failed', 'nope']])
  })

  it('stops when the session has expired and keeps everything queued', async () => {
    const outbox = makeOutbox({ tab: 'A', send: async () => ({ kind: 'unauthorized' }) })
    await outbox.enqueue(note('x'))

    expect(await outbox.process()).toBe('stopped-unauthorized')
    expect(await outbox.list()).toHaveLength(1)
  })

  describe('tab isolation', () => {
    it('never sends another live tab’s entries', async () => {
      const locks = new FakeLocks()
      const sentByB: string[] = []
      const tabA = makeOutbox({ tab: 'A', send: async () => ({ kind: 'retry' }), locks })
      const tabB = makeOutbox({
        tab: 'B',
        send: async (e) => {
          sentByB.push(e.label)
          return { kind: 'ok' }
        },
        locks,
      })
      locks.held.add(lockNameFor('A')) // tab A is alive
      await tabA.enqueue(note('from A', { label: 'A1' }))
      await tabB.enqueue(note('from B', { label: 'B1' }))

      await tabB.process()

      expect(sentByB).toEqual(['B1'])
      expect((await tabA.list()).map((e) => e.label)).toEqual(['A1'])
      expect(await tabB.list()).toEqual([])
    })

    it('does not show or send entries that belong to a different signed-in user', async () => {
      const locks = new FakeLocks()
      const sent: string[] = []
      const alice = makeOutbox({
        tab: 'A',
        user: 'alice',
        send: async () => ({ kind: 'retry' }),
        locks,
      })
      const bob = makeOutbox({
        tab: 'B',
        user: 'bob',
        send: async (e) => {
          sent.push(e.label)
          return { kind: 'ok' }
        },
        locks,
      })
      await alice.enqueue(note('private', { label: 'alice-1' }))

      await bob.process()

      expect(sent).toEqual([])
      expect(await bob.list()).toEqual([])
      expect(await alice.list()).toHaveLength(1)
    })

    it('adopts, in order, the entries of a tab that has gone away', async () => {
      const locks = new FakeLocks()
      const sentByB: string[] = []
      const tabA = makeOutbox({ tab: 'A', send: async () => ({ kind: 'retry' }), locks })
      const tabB = makeOutbox({
        tab: 'B',
        send: async (e) => {
          sentByB.push(e.label)
          return { kind: 'ok' }
        },
        locks,
      })
      await tabA.enqueue(note('1', { label: 'A1' }))
      await tabB.enqueue(note('2', { label: 'B1' }))
      await tabA.enqueue(note('3', { label: 'A2' }))
      // Tab A never held its lock in this test, i.e. it is dead.

      await tabB.process()

      expect(sentByB).toEqual(['A1', 'B1', 'A2'])
    })

    it('leaves adoption alone when locks are unavailable', async () => {
      const tabA = makeOutbox({ tab: 'A', send: async () => ({ kind: 'retry' }) })
      const tabB = makeOutbox({ tab: 'B', send: vi.fn(async () => ({ kind: 'ok' }) as SendResult) })
      await tabA.enqueue(note('x'))

      expect(await tabB.adoptOrphans()).toBe(0)
      expect(await tabA.list()).toHaveLength(1)
    })
  })

  it('coalesces overlapping process calls', async () => {
    let inFlight = 0
    let maxInFlight = 0
    const outbox = makeOutbox({
      tab: 'A',
      send: async () => {
        inFlight++
        maxInFlight = Math.max(maxInFlight, inFlight)
        await new Promise((r) => setTimeout(r, 5))
        inFlight--
        return { kind: 'ok' }
      },
    })
    await outbox.enqueue(note('1'))
    await outbox.enqueue(note('2'))

    await Promise.all([outbox.process(), outbox.process(), outbox.process()])

    expect(maxInFlight).toBe(1)
    expect(await outbox.list()).toEqual([])
  })
})
