import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { apiGet } from '../api/http'
import { keys } from '../api/queries'
import type { Note, NoteVisibility, Project } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { SyncBadge } from '../components/SyncBadge'
import {
  Badge,
  Button,
  Card,
  Dialog,
  EmptyState,
  ErrorState,
  LoadingState,
  Select,
  TextArea,
} from '../components/ui'
import { overlayPending, type WithSync } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'

export function NotesTab({ project }: { project: Project }) {
  const { me } = useAuth()
  const { entries, mutate } = useOutbox()
  const isClient = project.myRole === 'Client'
  const [body, setBody] = useState('')
  const [visibility, setVisibility] = useState<NoteVisibility>(isClient ? 'Client' : 'Internal')
  const [editing, setEditing] = useState<Note | null>(null)

  const notes = useQuery({
    queryKey: keys.notes(project.id),
    queryFn: ({ signal }) => apiGet<Note[]>(`/api/projects/${project.id}/notes`, signal),
  })

  const base = `/api/projects/${project.id}/notes`
  const invalidate = [[...keys.notes(project.id)]]

  const add = async (event: FormEvent) => {
    event.preventDefault()
    const text = body.trim()
    if (!text || !me) return
    setBody('')
    const now = new Date().toISOString()
    await mutate({
      method: 'POST',
      url: base,
      body: { body: text, visibility },
      label: `Add note “${text.length > 30 ? `${text.slice(0, 30)}…` : text}”`,
      optimistic: {
        kind: 'create',
        list: 'notes',
        item: {
          projectId: project.id,
          authorUserId: me.userId,
          authorName: me.displayName,
          body: text,
          visibility,
          createdAt: now,
          updatedAt: now,
          version: '',
        },
      },
      invalidate,
    })
  }

  const save = (note: Note, next: { body: string; visibility: NoteVisibility }) =>
    mutate({
      method: 'PUT',
      url: `${base}/${note.id}`,
      body: next,
      version: note.version,
      resourceKey: `note:${note.id}`,
      label: 'Edit note',
      optimistic: { kind: 'update', list: 'notes', id: note.id, patch: next },
      invalidate,
    })

  const remove = (note: Note) =>
    mutate({
      method: 'DELETE',
      url: `${base}/${note.id}`,
      version: note.version,
      resourceKey: `note:${note.id}`,
      label: 'Delete note',
      optimistic: { kind: 'delete', list: 'notes', id: note.id },
      invalidate,
    })

  if (notes.isPending) return <LoadingState label="Loading notes…" />
  if (notes.isError && !notes.data)
    return <ErrorState title="Could not load notes" onRetry={() => void notes.refetch()} />

  const { existing, created } = overlayPending(notes.data ?? [], entries, 'notes', (e) =>
    e.url.startsWith(base),
  )
  const all: WithSync<Note>[] = [...created.slice().reverse(), ...existing]

  return (
    <div className="space-y-4">
      <form onSubmit={(e) => void add(e)} className="space-y-2">
        <TextArea
          label="New note"
          value={body}
          maxLength={10000}
          onChange={(e) => setBody(e.target.value)}
        />
        <div className="flex flex-wrap items-end gap-2">
          {!isClient && (
            <Select
              label="Visibility"
              value={visibility}
              onChange={(e) => setVisibility(e.target.value as NoteVisibility)}
            >
              <option value="Internal">Internal (team only)</option>
              <option value="Client">Visible to clients</option>
            </Select>
          )}
          <Button type="submit" disabled={!body.trim()}>
            Add note
          </Button>
        </div>
      </form>

      {all.length === 0 ? (
        <EmptyState
          title="No notes yet"
          hint="Notes you add appear here for everyone with access."
        />
      ) : (
        <ul aria-label="Notes" className="space-y-2">
          {all.map((note) => {
            const mine = note.authorUserId === me?.userId
            const canChange = (mine || project.myRole === 'Lead') && note.sync?.kind !== 'create'
            return (
              <li key={note.id}>
                <Card className={note.sync?.kind === 'delete' ? 'opacity-50' : undefined}>
                  <div className="flex items-start justify-between gap-2">
                    <p
                      className={`whitespace-pre-wrap ${note.sync?.kind === 'delete' ? 'line-through' : ''}`}
                    >
                      {note.body}
                    </p>
                    <span className="flex shrink-0 items-center gap-1">
                      <SyncBadge sync={note.sync} />
                      {!isClient && (
                        <Badge tone={note.visibility === 'Client' ? 'green' : 'slate'}>
                          {note.visibility === 'Client' ? 'Client-visible' : 'Internal'}
                        </Badge>
                      )}
                    </span>
                  </div>
                  <div className="mt-2 flex items-center justify-between text-xs text-slate-500">
                    <span>
                      {note.authorName} · {new Date(note.createdAt).toLocaleString()}
                    </span>
                    {canChange && (
                      <span className="flex gap-1">
                        <Button
                          variant="ghost"
                          onClick={() => setEditing(note)}
                          aria-label="Edit note"
                        >
                          Edit
                        </Button>
                        <Button
                          variant="ghost"
                          onClick={() => void remove(note)}
                          aria-label="Delete note"
                        >
                          Delete
                        </Button>
                      </span>
                    )}
                  </div>
                </Card>
              </li>
            )
          })}
        </ul>
      )}

      {editing && (
        <EditNoteDialog
          note={editing}
          canChooseVisibility={!isClient}
          onClose={() => setEditing(null)}
          onSave={async (next) => {
            await save(editing, next)
            setEditing(null)
          }}
        />
      )}
    </div>
  )
}

function EditNoteDialog({
  note,
  canChooseVisibility,
  onClose,
  onSave,
}: {
  note: Note
  canChooseVisibility: boolean
  onClose: () => void
  onSave: (next: { body: string; visibility: NoteVisibility }) => Promise<void>
}) {
  const [body, setBody] = useState(note.body)
  const [visibility, setVisibility] = useState(note.visibility)
  return (
    <Dialog title="Edit note" onClose={onClose}>
      <div className="space-y-3">
        <TextArea
          label="Note text"
          value={body}
          maxLength={10000}
          onChange={(e) => setBody(e.target.value)}
        />
        {canChooseVisibility && (
          <Select
            label="Note visibility"
            value={visibility}
            onChange={(e) => setVisibility(e.target.value as NoteVisibility)}
          >
            <option value="Internal">Internal (team only)</option>
            <option value="Client">Visible to clients</option>
          </Select>
        )}
      </div>
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          disabled={!body.trim()}
          onClick={() => void onSave({ body: body.trim(), visibility })}
        >
          Save note
        </Button>
      </div>
    </Dialog>
  )
}
