import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { apiGet } from '../api/http'
import { keys } from '../api/queries'
import type { Milestone, MilestoneStatus, Project } from '../api/types'
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
  TextInput,
} from '../components/ui'
import { overlayPending, type WithSync } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'

const STATUSES: MilestoneStatus[] = ['Planned', 'InProgress', 'Done', 'Blocked']
const TONE = { Planned: 'slate', InProgress: 'indigo', Done: 'green', Blocked: 'red' } as const

type MilestoneBody = {
  title: string
  description: string
  dueDate: string | null
  status: MilestoneStatus
}

export function MilestonesTab({ project }: { project: Project }) {
  const { entries, mutate } = useOutbox()
  const canEdit = project.myRole === 'Lead' || project.myRole === 'Contributor'
  const [editing, setEditing] = useState<Milestone | null>(null)
  const [title, setTitle] = useState('')
  const [dueDate, setDueDate] = useState('')

  const milestones = useQuery({
    queryKey: keys.milestones(project.id),
    queryFn: ({ signal }) => apiGet<Milestone[]>(`/api/projects/${project.id}/milestones`, signal),
  })

  const base = `/api/projects/${project.id}/milestones`
  const invalidate = [[...keys.milestones(project.id)]]

  const add = async (event: FormEvent) => {
    event.preventDefault()
    const body: MilestoneBody = {
      title: title.trim(),
      description: '',
      dueDate: dueDate || null,
      status: 'Planned',
    }
    if (!body.title) return
    setTitle('')
    setDueDate('')
    await mutate({
      method: 'POST',
      url: base,
      body,
      label: `Add milestone “${body.title}”`,
      optimistic: {
        kind: 'create',
        list: 'milestones',
        item: { ...body, projectId: project.id, createdAt: new Date().toISOString(), version: '' },
      },
      invalidate,
    })
  }

  const save = (milestone: Milestone, body: MilestoneBody) =>
    mutate({
      method: 'PUT',
      url: `${base}/${milestone.id}`,
      body,
      version: milestone.version,
      resourceKey: `milestone:${milestone.id}`,
      label: `Update milestone “${body.title}”`,
      optimistic: { kind: 'update', list: 'milestones', id: milestone.id, patch: body },
      invalidate,
    })

  const remove = (milestone: Milestone) =>
    mutate({
      method: 'DELETE',
      url: `${base}/${milestone.id}`,
      version: milestone.version,
      resourceKey: `milestone:${milestone.id}`,
      label: `Delete milestone “${milestone.title}”`,
      optimistic: { kind: 'delete', list: 'milestones', id: milestone.id },
      invalidate,
    })

  if (milestones.isPending) return <LoadingState label="Loading milestones…" />
  if (milestones.isError && !milestones.data) {
    return (
      <ErrorState title="Could not load milestones" onRetry={() => void milestones.refetch()} />
    )
  }

  const { existing, created } = overlayPending(milestones.data ?? [], entries, 'milestones', (e) =>
    e.url.startsWith(base),
  )
  const all: WithSync<Milestone>[] = [...existing, ...created]

  return (
    <div className="space-y-4">
      {canEdit && (
        <form onSubmit={(e) => void add(e)} className="flex flex-wrap items-end gap-2">
          <TextInput
            label="New milestone"
            value={title}
            maxLength={200}
            onChange={(e) => setTitle(e.target.value)}
          />
          <TextInput
            label="Due date"
            type="date"
            value={dueDate}
            onChange={(e) => setDueDate(e.target.value)}
          />
          <Button type="submit" disabled={!title.trim()}>
            Add milestone
          </Button>
        </form>
      )}

      {all.length === 0 ? (
        <EmptyState
          title="No milestones yet"
          hint={canEdit ? 'Add the first milestone above.' : undefined}
        />
      ) : (
        <ul aria-label="Milestones" className="space-y-2">
          {all.map((m) => {
            const pending = m.sync?.kind === 'create'
            return (
              <li key={m.id}>
                <Card className={m.sync?.kind === 'delete' ? 'opacity-50' : undefined}>
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div>
                      <p
                        className={`font-medium ${m.sync?.kind === 'delete' ? 'line-through' : ''}`}
                      >
                        {m.title}
                      </p>
                      <p className="text-sm text-slate-500">
                        {m.dueDate ? `Due ${m.dueDate}` : 'No due date'}
                      </p>
                    </div>
                    <div className="flex items-center gap-2">
                      <SyncBadge sync={m.sync} />
                      <Badge tone={TONE[m.status]}>{m.status}</Badge>
                      {canEdit && !pending && (
                        <>
                          <Select
                            label="Status"
                            value={m.status}
                            onChange={(e) =>
                              void save(m, {
                                title: m.title,
                                description: m.description,
                                dueDate: m.dueDate,
                                status: e.target.value as MilestoneStatus,
                              })
                            }
                          >
                            {STATUSES.map((s) => (
                              <option key={s} value={s}>
                                {s}
                              </option>
                            ))}
                          </Select>
                          <Button
                            variant="secondary"
                            onClick={() => setEditing(m)}
                            aria-label={`Edit ${m.title}`}
                          >
                            Edit
                          </Button>
                          <Button
                            variant="ghost"
                            onClick={() => void remove(m)}
                            aria-label={`Delete ${m.title}`}
                          >
                            Delete
                          </Button>
                        </>
                      )}
                    </div>
                  </div>
                  {m.description && <p className="mt-2 text-sm">{m.description}</p>}
                </Card>
              </li>
            )
          })}
        </ul>
      )}

      {editing && (
        <EditMilestoneDialog
          milestone={editing}
          onClose={() => setEditing(null)}
          onSave={async (body) => {
            await save(editing, body)
            setEditing(null)
          }}
        />
      )}
    </div>
  )
}

/** Holds the version it was opened with: saving after someone else changed the milestone conflicts. */
function EditMilestoneDialog({
  milestone,
  onClose,
  onSave,
}: {
  milestone: Milestone
  onClose: () => void
  onSave: (body: MilestoneBody) => Promise<void>
}) {
  const [title, setTitle] = useState(milestone.title)
  const [description, setDescription] = useState(milestone.description)
  const [dueDate, setDueDate] = useState(milestone.dueDate ?? '')
  const [status, setStatus] = useState(milestone.status)

  return (
    <Dialog title="Edit milestone" onClose={onClose}>
      <div className="space-y-3">
        <TextInput
          label="Title"
          value={title}
          maxLength={200}
          onChange={(e) => setTitle(e.target.value)}
        />
        <TextArea
          label="Description"
          value={description}
          maxLength={4000}
          onChange={(e) => setDescription(e.target.value)}
        />
        <TextInput
          label="Due date"
          type="date"
          value={dueDate}
          onChange={(e) => setDueDate(e.target.value)}
        />
        <Select
          label="Milestone status"
          value={status}
          onChange={(e) => setStatus(e.target.value as MilestoneStatus)}
        >
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </Select>
      </div>
      <div className="mt-5 flex justify-end gap-2">
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button
          disabled={!title.trim()}
          onClick={() =>
            void onSave({
              title: title.trim(),
              description: description.trim(),
              dueDate: dueDate || null,
              status,
            })
          }
        >
          Save milestone
        </Button>
      </div>
    </Dialog>
  )
}
