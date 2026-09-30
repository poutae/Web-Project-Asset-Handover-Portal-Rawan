import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { apiGet } from '../api/http'
import { keys } from '../api/queries'
import type { OrgMember, Project, ProjectMember, ProjectRole } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { SyncBadge } from '../components/SyncBadge'
import { Badge, Button, EmptyState, ErrorState, LoadingState, Select } from '../components/ui'
import { overlayPending } from '../offline/optimistic'
import { useOutbox } from '../offline/OutboxProvider'

const STAFF_ROLES: ProjectRole[] = ['Lead', 'Contributor']

export function MembersTab({ project }: { project: Project }) {
  const { me } = useAuth()
  const { entries, mutate } = useOutbox()
  const canManage = project.myRole === 'Lead'
  const [userId, setUserId] = useState('')
  const [role, setRole] = useState<ProjectRole>('Contributor')

  const members = useQuery({
    queryKey: keys.members(project.id),
    queryFn: ({ signal }) => apiGet<ProjectMember[]>(`/api/projects/${project.id}/members`, signal),
  })
  const orgMembers = useQuery({
    queryKey: keys.orgMembers,
    queryFn: ({ signal }) => apiGet<OrgMember[]>('/api/org/members', signal),
    enabled: canManage && me?.role !== 'Client',
  })

  const base = `/api/projects/${project.id}/members`
  const invalidate = [[...keys.members(project.id)], [...keys.projects]]

  if (members.isPending) return <LoadingState label="Loading members…" />
  if (members.isError && !members.data)
    return <ErrorState title="Could not load members" onRetry={() => void members.refetch()} />

  const withId = (members.data ?? []).map((m) => ({ ...m, id: m.userId }))
  const { existing } = overlayPending(withId, entries, 'members', (e) => e.url.startsWith(base))
  const joined = new Set(withId.map((m) => m.userId))
  const candidates = (orgMembers.data ?? []).filter((p) => !joined.has(p.id))
  const chosen = candidates.find((p) => p.id === userId)
  const roleOptions: ProjectRole[] = chosen?.role === 'Client' ? ['Client'] : STAFF_ROLES

  const add = async (event: FormEvent) => {
    event.preventDefault()
    if (!chosen) return
    const effectiveRole = roleOptions.includes(role) ? role : (roleOptions[0] as ProjectRole)
    setUserId('')
    await mutate({
      method: 'POST',
      url: base,
      body: { userId: chosen.id, role: effectiveRole },
      label: `Add ${chosen.displayName} to the project`,
      invalidate,
    })
  }

  return (
    <div className="space-y-4">
      {canManage && (
        <form onSubmit={(e) => void add(e)} className="flex flex-wrap items-end gap-2">
          <Select label="Person" value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">Choose a person…</option>
            {candidates.map((p) => (
              <option key={p.id} value={p.id}>
                {p.displayName} ({p.role})
              </option>
            ))}
          </Select>
          <Select
            label="Project role"
            value={roleOptions.includes(role) ? role : roleOptions[0]}
            onChange={(e) => setRole(e.target.value as ProjectRole)}
          >
            {roleOptions.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </Select>
          <Button type="submit" disabled={!chosen}>
            Add member
          </Button>
        </form>
      )}

      {existing.length === 0 ? (
        <EmptyState title="No members" />
      ) : (
        <ul aria-label="Members" className="space-y-2">
          {existing.map((m) => (
            <li
              key={m.userId}
              className={`flex items-center justify-between rounded-md bg-white p-3 ring-1 ring-slate-200 dark:bg-slate-900 dark:ring-slate-800 ${m.sync?.kind === 'delete' ? 'opacity-50' : ''}`}
            >
              <span>
                {m.displayName} <span className="text-sm text-slate-500">{m.email}</span>
              </span>
              <span className="flex items-center gap-2">
                <SyncBadge sync={m.sync} />
                <Badge tone="indigo">{m.role}</Badge>
                {canManage && m.userId !== me?.userId && (
                  <Button
                    variant="ghost"
                    aria-label={`Remove ${m.displayName}`}
                    onClick={() =>
                      void mutate({
                        method: 'DELETE',
                        url: `${base}/${m.userId}`,
                        version: m.version,
                        resourceKey: `member:${project.id}:${m.userId}`,
                        label: `Remove ${m.displayName} from the project`,
                        optimistic: { kind: 'delete', list: 'members', id: m.userId },
                        invalidate,
                      })
                    }
                  >
                    Remove
                  </Button>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
