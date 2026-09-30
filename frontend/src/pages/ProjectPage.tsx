import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, apiGet } from '../api/http'
import { keys } from '../api/queries'
import type { Project, ProjectStatus } from '../api/types'
import { SyncBadge } from '../components/SyncBadge'
import {
  Badge,
  Button,
  Dialog,
  ErrorState,
  LoadingState,
  Select,
  TextArea,
  TextInput,
} from '../components/ui'
import { overlayPending } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'
import { MembersTab } from './MembersTab'
import { MilestonesTab } from './MilestonesTab'
import { NotesTab } from './NotesTab'

const TABS = [
  { id: 'milestones', label: 'Milestones' },
  { id: 'notes', label: 'Notes' },
  { id: 'members', label: 'Members' },
] as const

const STATUSES: ProjectStatus[] = ['Active', 'OnHold', 'Completed', 'Archived']

export function ProjectPage() {
  const { projectId = '' } = useParams()
  const [params, setParams] = useSearchParams()
  const { entries } = useOutbox()
  const project = useQuery({
    queryKey: keys.project(projectId),
    queryFn: ({ signal }) => apiGet<Project>(`/api/projects/${projectId}`, signal),
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 1,
  })
  const [editing, setEditing] = useState<Project | null>(null)

  if (project.isPending) return <LoadingState label="Loading project…" />
  if (project.isError && !project.data) {
    const notFound = project.error instanceof ApiError && project.error.status === 404
    return (
      <div className="space-y-3">
        <Link to="/" className="text-sm text-indigo-600 hover:underline">
          ← All projects
        </Link>
        <ErrorState
          title={notFound ? 'Project not found' : 'Could not load this project'}
          message={
            notFound
              ? 'It may have been removed, or you may not have access.'
              : 'Check your connection and try again.'
          }
          onRetry={notFound ? undefined : () => void project.refetch()}
        />
      </div>
    )
  }

  const { existing } = overlayPending([project.data as Project], entries, 'projects')
  const shown = existing[0] as Project & { sync?: Parameters<typeof SyncBadge>[0]['sync'] }
  const tab = TABS.find((t) => t.id === params.get('tab'))?.id ?? 'milestones'
  const canManage = shown.myRole === 'Lead'

  return (
    <div className="space-y-4">
      <Link to="/" className="text-sm text-indigo-600 hover:underline">
        ← All projects
      </Link>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold">
            {shown.name} <Badge>{shown.status}</Badge> <SyncBadge sync={shown.sync} />
          </h1>
          {shown.description && <p className="mt-1 text-slate-500">{shown.description}</p>}
        </div>
        {canManage && (
          <Button variant="secondary" onClick={() => setEditing(project.data as Project)}>
            Edit project
          </Button>
        )}
      </div>

      <div
        role="tablist"
        aria-label="Project sections"
        className="flex gap-1 border-b border-slate-200 dark:border-slate-800"
      >
        {TABS.map((t) => (
          <button
            key={t.id}
            role="tab"
            type="button"
            aria-selected={tab === t.id}
            onClick={() => setParams({ tab: t.id }, { replace: true })}
            className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium ${tab === t.id ? 'border-indigo-600 text-indigo-600' : 'border-transparent text-slate-500 hover:text-slate-700'}`}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div role="tabpanel">
        {tab === 'milestones' && <MilestonesTab project={shown} />}
        {tab === 'notes' && <NotesTab project={shown} />}
        {tab === 'members' && <MembersTab project={shown} />}
      </div>

      {editing && <EditProjectDialog project={editing} onClose={() => setEditing(null)} />}
    </div>
  )
}

/** The dialog keeps the version it was opened with, so saving after someone else's change conflicts. */
function EditProjectDialog({ project, onClose }: { project: Project; onClose: () => void }) {
  const { mutate } = useOutbox()
  const [name, setName] = useState(project.name)
  const [description, setDescription] = useState(project.description)
  const [status, setStatus] = useState<ProjectStatus>(project.status)

  const save = async () => {
    const body = { name: name.trim(), description: description.trim(), status }
    await mutate({
      method: 'PUT',
      url: `/api/projects/${project.id}`,
      body,
      version: project.version,
      resourceKey: `project:${project.id}`,
      label: `Edit project “${body.name}”`,
      optimistic: { kind: 'update', list: 'projects', id: project.id, patch: body },
      invalidate: [[...keys.project(project.id)], [...keys.projects]],
    })
    onClose()
  }

  return (
    <Dialog title="Edit project" onClose={onClose}>
      <div className="space-y-3">
        <TextInput
          label="Name"
          value={name}
          maxLength={200}
          onChange={(e) => setName(e.target.value)}
        />
        <TextArea
          label="Description"
          value={description}
          maxLength={4000}
          onChange={(e) => setDescription(e.target.value)}
        />
        <Select
          label="Status"
          value={status}
          onChange={(e) => setStatus(e.target.value as ProjectStatus)}
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
        <Button disabled={!name.trim()} onClick={() => void save()}>
          Save project
        </Button>
      </div>
    </Dialog>
  )
}
