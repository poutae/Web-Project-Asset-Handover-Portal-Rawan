import { useOutbox } from '../offline/OutboxProvider'
import type { OutboxEntry } from '../offline/types'
import { Button, Dialog } from './ui'

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  return typeof value === 'object' ? JSON.stringify(value) : String(value)
}

function rows(entry: OutboxEntry): { field: string; mine: unknown; theirs: unknown }[] {
  const mine = (entry.body ?? {}) as Record<string, unknown>
  const theirs = (entry.conflictCurrent ?? {}) as Record<string, unknown>
  return Object.keys(mine).map((field) => ({ field, mine: mine[field], theirs: theirs[field] }))
}

/**
 * Opens whenever one of the user's queued changes was rejected because somebody else changed the same
 * item first. Nothing is overwritten until the user chooses.
 */
export function ConflictDialog() {
  const { entries, keepMine, discard } = useOutbox()
  const entry = entries.find((e) => e.status === 'conflict')
  if (!entry) return null

  const deleting = entry.method === 'DELETE'
  const gone = entry.conflictCurrent === undefined

  return (
    <Dialog title="Edit conflict">
      <p className="text-sm">
        <strong>{entry.label}</strong> could not be saved because someone else changed this item
        first.
      </p>
      {!gone && !deleting && (
        <table className="mt-3 w-full text-left text-sm">
          <thead>
            <tr className="text-slate-500">
              <th className="py-1 pr-2 font-medium">Field</th>
              <th className="py-1 pr-2 font-medium">Your change</th>
              <th className="py-1 font-medium">Current on server</th>
            </tr>
          </thead>
          <tbody>
            {rows(entry).map((row) => (
              <tr
                key={row.field}
                className="border-t border-slate-200 align-top dark:border-slate-800"
              >
                <td className="py-1 pr-2">{row.field}</td>
                <td className="py-1 pr-2" data-testid={`mine-${row.field}`}>
                  {show(row.mine)}
                </td>
                <td className="py-1" data-testid={`theirs-${row.field}`}>
                  {show(row.theirs)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {deleting && <p className="mt-3 text-sm">The item was modified after you last saw it.</p>}
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={() => void discard(entry)}>
          Use server version
        </Button>
        {!gone && (
          <Button onClick={() => void keepMine(entry)}>
            {deleting ? 'Delete anyway' : 'Keep my change'}
          </Button>
        )}
      </div>
    </Dialog>
  )
}
