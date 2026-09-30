import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { ApiError, apiGet, apiRequest } from '../api/http'
import { keys } from '../api/queries'
import type { CreatedInvitation, Invitation, OrgMember } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import {
  Badge,
  Button,
  Card,
  EmptyState,
  ErrorState,
  LoadingState,
  Select,
  TextInput,
  errorMessage,
} from '../components/ui'
import { useOutbox } from '../offline/OutboxProvider'

export function TeamPage() {
  const { me } = useAuth()
  const { online } = useOutbox()
  const queryClient = useQueryClient()
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<'Member' | 'Client'>('Member')
  const [created, setCreated] = useState<CreatedInvitation | null>(null)

  const members = useQuery({
    queryKey: keys.orgMembers,
    queryFn: ({ signal }) => apiGet<OrgMember[]>('/api/org/members', signal),
  })
  const invitations = useQuery({
    queryKey: keys.invitations,
    queryFn: ({ signal }) => apiGet<Invitation[]>('/api/org/invitations', signal),
  })

  // Invitations are online-only: the response contains the one-time secret, which should never be
  // written to the offline queue on disk.
  const invite = useMutation({
    mutationFn: async () =>
      (
        await apiRequest<CreatedInvitation>('/api/org/invitations', {
          method: 'POST',
          body: { email: email.trim(), role },
          idempotencyKey: crypto.randomUUID(),
        })
      ).data as CreatedInvitation,
    onSuccess: (result) => {
      setCreated(result)
      setEmail('')
      void queryClient.invalidateQueries({ queryKey: keys.invitations })
    },
  })
  const revoke = useMutation({
    mutationFn: (id: string) =>
      apiRequest(`/api/org/invitations/${id}`, {
        method: 'DELETE',
        idempotencyKey: crypto.randomUUID(),
      }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: keys.invitations }),
  })

  if (me?.role !== 'Admin')
    return <ErrorState title="Admins only" message="You do not have access to team management." />

  const submit = (event: FormEvent) => {
    event.preventDefault()
    invite.mutate()
  }
  const link = created ? `${window.location.origin}/accept-invite?token=${created.token}` : ''

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold">Team</h1>

      <section aria-labelledby="invite-heading" className="space-y-3">
        <h2 id="invite-heading" className="font-medium">
          Invite someone
        </h2>
        <Card>
          <form onSubmit={submit} className="flex flex-wrap items-end gap-3">
            <TextInput
              label="Invitee email"
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
            <Select
              label="Role"
              value={role}
              onChange={(e) => setRole(e.target.value as 'Member' | 'Client')}
            >
              <option value="Member">Team member</option>
              <option value="Client">Client</option>
            </Select>
            <Button type="submit" disabled={invite.isPending || !online}>
              {invite.isPending ? 'Creating…' : 'Create invitation'}
            </Button>
          </form>
          {!online && <p className="mt-2 text-sm text-amber-700">Invitations need a connection.</p>}
          {invite.isError && (
            <p role="alert" className="mt-2 text-sm text-red-600">
              {invite.error instanceof ApiError
                ? (invite.error.fieldErrors.email?.[0] ?? invite.error.message)
                : errorMessage(invite.error)}
            </p>
          )}
        </Card>
        {created && (
          <Card>
            <p className="text-sm">
              Share this link with <strong>{created.email}</strong>. It is shown only once and
              expires {new Date(created.expiresAt).toLocaleDateString()}.
            </p>
            <input
              readOnly
              aria-label="Invitation link"
              value={link}
              className="mt-2 w-full rounded-md bg-slate-100 px-2 py-1 font-mono text-xs dark:bg-slate-800"
              onFocus={(e) => e.currentTarget.select()}
            />
          </Card>
        )}
      </section>

      <section aria-labelledby="pending-heading" className="space-y-3">
        <h2 id="pending-heading" className="font-medium">
          Invitations
        </h2>
        {invitations.isPending ? (
          <LoadingState />
        ) : invitations.isError && !invitations.data ? (
          <ErrorState
            message="Could not load invitations."
            onRetry={() => void invitations.refetch()}
          />
        ) : invitations.data?.length === 0 ? (
          <EmptyState title="No invitations yet" />
        ) : (
          <ul className="space-y-2">
            {invitations.data?.map((item) => (
              <li
                key={item.id}
                className="flex items-center justify-between gap-2 rounded-md bg-white p-3 ring-1 ring-slate-200 dark:bg-slate-900 dark:ring-slate-800"
              >
                <span>
                  {item.email} <Badge>{item.role}</Badge>
                </span>
                <span className="flex items-center gap-2">
                  <Badge
                    tone={
                      item.status === 'Pending'
                        ? 'amber'
                        : item.status === 'Accepted'
                          ? 'green'
                          : 'slate'
                    }
                  >
                    {item.status}
                  </Badge>
                  {item.status === 'Pending' && (
                    <Button
                      variant="ghost"
                      disabled={!online || revoke.isPending}
                      onClick={() => revoke.mutate(item.id)}
                    >
                      Revoke
                    </Button>
                  )}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-labelledby="members-heading" className="space-y-3">
        <h2 id="members-heading" className="font-medium">
          People
        </h2>
        {members.isPending ? (
          <LoadingState />
        ) : members.isError && !members.data ? (
          <ErrorState message="Could not load people." onRetry={() => void members.refetch()} />
        ) : (
          <ul className="space-y-2">
            {members.data?.map((person) => (
              <li
                key={person.id}
                className="flex items-center justify-between rounded-md bg-white p-3 ring-1 ring-slate-200 dark:bg-slate-900 dark:ring-slate-800"
              >
                <span>
                  {person.displayName}{' '}
                  <span className="text-sm text-slate-500">{person.email}</span>
                </span>
                <Badge tone="indigo">{person.role}</Badge>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
