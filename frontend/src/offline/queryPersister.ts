import type { PersistedClient, Persister } from '@tanstack/react-query-persist-client'
import { getDb } from './storage'

const KEY = 'react-query'

/**
 * Keeps the query cache in IndexedDB so that a reload while offline can still show the last data the
 * user saw. It is wiped on sign-out (see `clearPersistedCache`).
 */
export const indexedDbPersister: Persister = {
  async persistClient(client: PersistedClient) {
    const db = await getDb()
    await db.put('queryCache', client, KEY)
  },
  async restoreClient() {
    const db = await getDb()
    return (await db.get('queryCache', KEY)) as PersistedClient | undefined
  },
  async removeClient() {
    const db = await getDb()
    await db.delete('queryCache', KEY)
  },
}

export async function clearPersistedCache(): Promise<void> {
  await indexedDbPersister.removeClient()
}
