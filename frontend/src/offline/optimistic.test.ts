import { describe, expect, it } from 'vitest'
import { overlayPending } from './optimistic'
import type { OutboxEntry } from './types'

const entry = (patch: Partial<OutboxEntry>): OutboxEntry => ({
  id: crypto.randomUUID(),
  ownerTabId: 't',
  userId: 'u',
  method: 'POST',
  url: '/api/projects/p1/notes',
  label: 'x',
  invalidate: [],
  createdAt: 0,
  status: 'pending',
  attempts: 0,
  ...patch,
})

const items = [
  { id: 'a', body: 'first' },
  { id: 'b', body: 'second' },
]

describe('overlayPending', () => {
  it('returns server data untouched when nothing is queued', () => {
    const { existing, created } = overlayPending(items, [], 'notes')
    expect(existing).toEqual(items)
    expect(created).toEqual([])
  })

  it('shows queued edits with their new values and a queued marker', () => {
    const queued = entry({
      optimistic: { kind: 'update', list: 'notes', id: 'a', patch: { body: 'edited' } },
    })
    const { existing } = overlayPending(items, [queued], 'notes')

    expect(existing[0]).toMatchObject({
      id: 'a',
      body: 'edited',
      sync: { kind: 'update', state: 'queued', entryId: queued.id },
    })
    expect(existing[1]).toEqual(items[1])
  })

  it('applies queued edits in order, so the latest wins', () => {
    const one = entry({
      optimistic: { kind: 'update', list: 'notes', id: 'a', patch: { body: 'one' } },
    })
    const two = entry({
      optimistic: { kind: 'update', list: 'notes', id: 'a', patch: { body: 'two' } },
    })

    expect(overlayPending(items, [one, two], 'notes').existing[0]?.body).toBe('two')
  })

  it('marks queued deletes', () => {
    const { existing } = overlayPending(
      items,
      [entry({ optimistic: { kind: 'delete', list: 'notes', id: 'b' } })],
      'notes',
    )
    expect(existing[1]?.sync?.kind).toBe('delete')
  })

  it('lists queued creates in creation order, keyed by the entry id', () => {
    const first = entry({ optimistic: { kind: 'create', list: 'notes', item: { body: 'new 1' } } })
    const second = entry({ optimistic: { kind: 'create', list: 'notes', item: { body: 'new 2' } } })

    const { created } = overlayPending(items, [first, second], 'notes')

    expect(created.map((c) => [c.id, c.body])).toEqual([
      [first.id, 'new 1'],
      [second.id, 'new 2'],
    ])
    expect(created[0]?.sync).toMatchObject({ kind: 'create', state: 'queued' })
  })

  it('reflects conflicts and failures in the marker', () => {
    const conflict = entry({
      status: 'conflict',
      optimistic: { kind: 'update', list: 'notes', id: 'a', patch: {} },
    })
    const failed = entry({
      status: 'failed',
      optimistic: { kind: 'delete', list: 'notes', id: 'b' },
    })

    const { existing } = overlayPending(items, [conflict, failed], 'notes')

    expect(existing[0]?.sync?.state).toBe('conflict')
    expect(existing[1]?.sync?.state).toBe('failed')
  })

  it('ignores entries for other lists and out-of-scope entries', () => {
    const otherList = entry({ optimistic: { kind: 'delete', list: 'milestones', id: 'a' } })
    const otherProject = entry({
      url: '/api/projects/p2/notes',
      optimistic: { kind: 'delete', list: 'notes', id: 'a' },
    })

    const { existing } = overlayPending(items, [otherList, otherProject], 'notes', (e) =>
      e.url.startsWith('/api/projects/p1'),
    )

    expect(existing).toEqual(items)
  })
})
