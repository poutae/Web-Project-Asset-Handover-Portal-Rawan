import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { apiGet } from '../api/http'
import { keys } from '../api/queries'
import type { Project } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { SyncBadge } from '../components/SyncBadge'
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  LoadingState,
  TextInput,
} from '../components/ui'
import { overlayPending } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'

export function ProjectsPage() {
  const { me } = useAuth()
  const { entries, mutate } = useOutbox()
  const [name, setName] = useState('')
  const projects = useQuery({
    queryKey: keys.projects,
    queryFn: ({ signal }) => apiGet<Project[]>('/api/projects', signal),
  })

  const canCreate = me?.role !== 'Client'

  const create = async (event: FormEvent) => {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed) return
    setName('')
    await mutate({
      method: 'POST',
      url: '/api/projects',
      body: { name: trimmed, description: '' },
      label: `Create project “${trimmed}”`,
      optimistic: {
        kind: 'create',
        list: 'projects',
        item: {
          name: trimmed,
          description: '',
          status: 'Active',
          createdAt: new Date().toISOString(),
          myRole: 'Lead',
          version: '',
        },
      },
      invalidate: [[...keys.projects]],
    })
  }

  if (projects.isPending) return <LoadingState label="Loading projects…" />
  if (projects.isError && !projects.data) {
    return (
      <ErrorState
        title="Could not load projects"
        message="Check your connection and try again."
        onRetry={() => void projects.refetch()}
      />
    )
  }

  const { existing, created } = overlayPending(projects.data ?? [], entries, 'projects')
  const all = [...created.slice().reverse(), ...existing]

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h1 className="text-xl font-semibold">Projects</h1>
        {canCreate && (
          <form onSubmit={(e) => void create(e)} className="flex items-end gap-2">
            <TextInput
              label="Project name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              maxLength={200}
            />
            <Button type="submit" disabled={!name.trim()}>
              Create project
            </Button>
          </form>
        )}
      </div>
      {all.length === 0 ? (
        <EmptyState
          title="No projects yet"
          hint={
            canCreate
              ? 'Create your first project to get started.'
              : 'You have not been added to any project yet.'
          }
        />
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2" aria-label="Projects">
          {all.map((project) => (
            <li key={project.id}>
              <Card>
                <div className="flex items-start justify-between gap-2">
                  {project.sync?.kind === 'create' ? (
                    <span className="font-medium">{project.name}</span>
                  ) : (
                    <Link
                      to={`/projects/${project.id}`}
                      className="font-medium text-indigo-600 hover:underline"
                    >
                      {project.name}
                    </Link>
                  )}
                  <span className="flex gap-1">
                    <SyncBadge sync={project.sync} />
                    <Badge>{project.status}</Badge>
                  </span>
                </div>
                {project.description && (
                  <p className="mt-1 text-sm text-slate-500">{project.description}</p>
                )}
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
