import { useState } from 'react'
import { useOutbox } from '../offline/OutboxProvider'
import { Button } from './ui'

/** The header indicator for the offline queue: what is waiting, and what needs attention. */
export function SyncStatus() {
  const { entries, online, syncing, sessionExpired, retry, discard } = useOutbox()
  const [open, setOpen] = useState(false)
  const waiting = entries.filter((e) => e.status === 'pending').length
  const failed = entries.filter((e) => e.status === 'failed')

  let text = 'All changes synced'
  if (sessionExpired) text = 'Sign in again to sync your changes'
  else if (waiting > 0) text = `${waiting} ${waiting === 1 ? 'change' : 'changes'} waiting to sync`
  else if (failed.length > 0)
    text = `${failed.length} ${failed.length === 1 ? 'change' : 'changes'} not saved`
  else if (syncing) text = 'Syncing…'

  return (
    <div className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        data-testid="sync-status"
        data-pending={waiting}
        className="rounded-md px-2 py-1 text-sm text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
      >
        {text}
        {!online && waiting > 0 ? ' (offline)' : ''}
      </button>
      {open && entries.length > 0 && (
        <ul
          aria-label="Queued changes"
          className="absolute right-0 z-40 mt-1 w-80 space-y-1 rounded-md bg-white p-2 text-sm shadow-lg ring-1 ring-slate-200 dark:bg-slate-900 dark:ring-slate-700"
        >
          {entries.map((entry) => (
            <li key={entry.id} className="flex items-center justify-between gap-2">
              <span className="truncate">
                {entry.label}
                {entry.status === 'failed' && (
                  <span className="block text-xs text-red-600">{entry.error}</span>
                )}
              </span>
              {entry.status === 'failed' ? (
                <span className="flex gap-1">
                  <Button variant="ghost" onClick={() => void retry(entry)}>
                    Retry
                  </Button>
                  <Button variant="ghost" onClick={() => void discard(entry)}>
                    Discard
                  </Button>
                </span>
              ) : (
                <span className="text-xs text-slate-500">
                  {entry.status === 'conflict' ? 'conflict' : 'queued'}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
