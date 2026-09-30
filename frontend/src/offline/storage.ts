import { openDB, type DBSchema, type IDBPDatabase } from 'idb'
import type { OutboxEntry, OutboxStorage } from './types'

interface PortalDb extends DBSchema {
  outbox: {
    key: number
    value: OutboxEntry
    indexes: { byOwner: string }
  }
  queryCache: {
    key: string
    value: unknown
  }
}

export const DB_NAME = 'portal-offline'

let dbPromise: Promise<IDBPDatabase<PortalDb>> | null = null

export function getDb(): Promise<IDBPDatabase<PortalDb>> {
  dbPromise ??= openDB<PortalDb>(DB_NAME, 1, {
    upgrade(db) {
      const outbox = db.createObjectStore('outbox', { keyPath: 'seq', autoIncrement: true })
      outbox.createIndex('byOwner', 'ownerTabId')
      db.createObjectStore('queryCache')
    },
  })
  return dbPromise
}

/** Test hook: drops the cached connection so a fresh database can be opened. */
export function resetDbConnection(): void {
  void dbPromise?.then((db) => db.close())
  dbPromise = null
}

export const indexedDbOutboxStorage: OutboxStorage = {
  async add(entry) {
    const db = await getDb()
    const seq = (await db.add('outbox', entry as OutboxEntry)) as number
    return { ...entry, seq }
  },

  async all() {
    const db = await getDb()
    return (await db.getAll('outbox')).sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0))
  },

  async update(seq, patch) {
    const db = await getDb()
    const tx = db.transaction('outbox', 'readwrite')
    const existing = await tx.store.get(seq)
    if (existing) await tx.store.put({ ...existing, ...patch, seq })
    await tx.done
  },

  async remove(seq) {
    const db = await getDb()
    await db.delete('outbox', seq)
  },

  async reassign(from, to, userId) {
    const db = await getDb()
    const tx = db.transaction('outbox', 'readwrite')
    let moved = 0
    for (const entry of await tx.store.index('byOwner').getAll(from)) {
      if (entry.userId !== userId) continue
      await tx.store.put({ ...entry, ownerTabId: to })
      moved++
    }
    await tx.done
    return moved
  },
}
