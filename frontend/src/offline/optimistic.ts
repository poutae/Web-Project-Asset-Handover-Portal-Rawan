import type { ListName, OutboxEntry } from './types'

export interface SyncInfo {
  entryId: string
  kind: 'create' | 'update' | 'delete'
  state: 'queued' | 'conflict' | 'failed'
}

export type WithSync<T> = T & { sync?: SyncInfo }

function stateOf(entry: OutboxEntry): SyncInfo['state'] {
  return entry.status === 'pending' ? 'queued' : entry.status
}

/**
 * Overlays not-yet-confirmed changes on top of server data: queued edits show their new values,
 * queued deletes are marked, and queued creates appear (in creation order) as extra items.
 */
export function overlayPending<T extends { id: string }>(
  items: readonly T[],
  entries: readonly OutboxEntry[],
  list: ListName,
  scope?: (entry: OutboxEntry) => boolean,
): { existing: WithSync<T>[]; created: WithSync<T>[] } {
  const relevant = entries.filter((e) => e.optimistic?.list === list && (scope?.(e) ?? true))

  const existing = items.map((item): WithSync<T> => {
    let result: WithSync<T> = item
    for (const entry of relevant) {
      const optimistic = entry.optimistic
      if (!optimistic || optimistic.kind === 'create' || optimistic.id !== item.id) continue
      result =
        optimistic.kind === 'update'
          ? {
              ...result,
              ...optimistic.patch,
              sync: { entryId: entry.id, kind: 'update', state: stateOf(entry) },
            }
          : { ...result, sync: { entryId: entry.id, kind: 'delete', state: stateOf(entry) } }
    }
    return result
  })

  const created = relevant.flatMap((entry): WithSync<T>[] => {
    const optimistic = entry.optimistic
    if (optimistic?.kind !== 'create') return []
    return [
      {
        ...(optimistic.item as Record<string, unknown>),
        id: entry.id,
        sync: { entryId: entry.id, kind: 'create', state: stateOf(entry) },
      } as unknown as WithSync<T>,
    ]
  })

  return { existing, created }
}
