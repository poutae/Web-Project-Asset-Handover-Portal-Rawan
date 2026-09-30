import type { SyncInfo } from '../offline/optimistic'
import { Badge } from './ui'

/** Shows that a change has not been confirmed by the server yet (or needs attention). */
export function SyncBadge({ sync }: { sync?: SyncInfo }) {
  if (!sync) return null
  if (sync.state === 'conflict')
    return (
      <Badge tone="red" data-testid="sync-badge">
        Conflict
      </Badge>
    )
  if (sync.state === 'failed')
    return (
      <Badge tone="red" data-testid="sync-badge">
        Not saved
      </Badge>
    )
  return (
    <Badge tone="amber" data-testid="sync-badge">
      Waiting to sync
    </Badge>
  )
}
