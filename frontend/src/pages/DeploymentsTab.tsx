import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, apiGet, apiRequest } from '../api/http'
import { keys } from '../api/queries'
import type {
  Deployment,
  DeploymentEnvironment,
  DeploymentLogs,
  DeploymentStatus,
  Project,
} from '../api/types'
import { useOutbox } from '../offline/OutboxProvider'
import {
  Badge,
  Button,
  Card,
  Dialog,
  EmptyState,
  ErrorState,
  LoadingState,
  TextInput,
  errorMessage,
} from '../components/ui'

const ACTIVE: DeploymentStatus[] = ['Queued', 'Building', 'Activating', 'HealthChecking']
const TONE = {
  Queued: 'slate',
  Building: 'indigo',
  Activating: 'indigo',
  HealthChecking: 'indigo',
  Succeeded: 'green',
  Failed: 'red',
  Cancelled: 'slate',
} as const
const LABEL: Record<DeploymentStatus, string> = {
  Queued: 'Queued',
  Building: 'Building',
  Activating: 'Going live',
  HealthChecking: 'Checking health',
  Succeeded: 'Live',
  Failed: 'Failed',
  Cancelled: 'Cancelled',
}

/**
 * Deployments are online-only: a build starts on the server, so unlike notes they are never queued on the
 * device. Everyone on the project sees status; staff deploy and read logs; leads manage environments.
 */
export function DeploymentsTab({ project }: { project: Project }) {
  const { online } = useOutbox()
  const isClient = project.myRole === 'Client'
  const canManage = project.myRole === 'Lead'
  const [editing, setEditing] = useState<DeploymentEnvironment | 'new' | null>(null)

  const environments = useQuery({
    queryKey: keys.environments(project.id),
    queryFn: ({ signal }) =>
      apiGet<DeploymentEnvironment[]>(`/api/projects/${project.id}/environments`, signal),
  })

  if (environments.isPending) return <LoadingState label="Loading environments…" />
  if (environments.isError && !environments.data) {
    return (
      <ErrorState title="Could not load environments" onRetry={() => void environments.refetch()} />
    )
  }

  return (
    <div className="space-y-4">
      {!online && (
        <p className="text-sm text-amber-700">
          Deploying needs a connection. Nothing here is queued while offline.
        </p>
      )}
      {canManage && (
        <div>
          <Button onClick={() => setEditing('new')}>Add environment</Button>
        </div>
      )}
      {environments.data.length === 0 ? (
        <EmptyState
          title="No environments yet"
          hint={
            canManage
              ? 'Add an environment (for example “production”) to deploy this project to a live URL.'
              : 'Nothing has been set up for deployment yet.'
          }
        />
      ) : (
        environments.data.map((environment) => (
          <EnvironmentCard
            key={environment.id}
            project={project}
            environment={environment}
            isClient={isClient}
            canManage={canManage}
            online={online}
            onEdit={() => setEditing(environment)}
          />
        ))
      )}
      {editing && (
        <EnvironmentDialog
          project={project}
          environment={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  )
}

function EnvironmentCard({
  project,
  environment,
  isClient,
  canManage,
  online,
  onEdit,
}: {
  project: Project
  environment: DeploymentEnvironment
  isClient: boolean
  canManage: boolean
  online: boolean
  onEdit: () => void
}) {
  const queryClient = useQueryClient()
  const canDeploy = project.myRole === 'Lead' || project.myRole === 'Contributor'
  const [logFor, setLogFor] = useState<Deployment | null>(null)
  const base = `/api/projects/${project.id}/environments/${environment.id}`

  const deployments = useQuery({
    queryKey: keys.deployments(project.id, environment.id),
    queryFn: ({ signal }) => apiGet<Deployment[]>(`${base}/deployments`, signal),
    // Realtime pushes changes; polling is only a safety net while something is running.
    refetchInterval: (query) =>
      query.state.data?.some((d) => ACTIVE.includes(d.status)) ? 3000 : false,
  })
  const active = deployments.data?.find((d) => ACTIVE.includes(d.status))

  const act = useMutation({
    mutationFn: (request: { path: string; body?: unknown }) =>
      apiRequest(`${base}/${request.path}`, {
        method: 'POST',
        body: request.body ?? {},
        idempotencyKey: crypto.randomUUID(),
      }),
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: keys.deployments(project.id, environment.id) })
      void queryClient.invalidateQueries({ queryKey: keys.environments(project.id) })
    },
  })
  const remove = useMutation({
    mutationFn: () =>
      apiRequest(base, {
        method: 'DELETE',
        version: environment.version,
        idempotencyKey: crypto.randomUUID(),
      }),
    onSuccess: () =>
      void queryClient.invalidateQueries({ queryKey: keys.environments(project.id) }),
  })

  const current = deployments.data?.find((d) => d.isCurrent)

  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="font-medium">{environment.name}</h3>
          {environment.url && (
            <a
              href={environment.url}
              target="_blank"
              rel="noreferrer"
              className="break-all text-sm text-indigo-600 hover:underline"
            >
              {environment.url}
            </a>
          )}
          {!isClient && environment.repositoryUrl && (
            <p className="mt-1 break-all text-xs text-slate-500">
              {environment.repositoryUrl} · {environment.branch}
              {environment.hasAccessToken ? ' · access token set' : ''}
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          {current ? (
            <Badge tone="green">Live · {current.commitSha?.slice(0, 7) ?? 'unknown'}</Badge>
          ) : (
            <Badge>Not live</Badge>
          )}
          {canDeploy && (
            <Button
              disabled={!online || act.isPending || Boolean(active)}
              onClick={() => act.mutate({ path: 'deployments' })}
              aria-label={`Deploy ${environment.name}`}
            >
              Deploy now
            </Button>
          )}
          {canManage && (
            <>
              <Button
                variant="secondary"
                onClick={onEdit}
                aria-label={`Edit environment ${environment.name}`}
              >
                Edit
              </Button>
              <Button
                variant="ghost"
                disabled={!online || remove.isPending}
                onClick={() => {
                  if (
                    window.confirm(
                      `Delete “${environment.name}”? Its site goes offline and its history is removed.`,
                    )
                  )
                    remove.mutate()
                }}
                aria-label={`Delete environment ${environment.name}`}
              >
                Delete
              </Button>
            </>
          )}
        </div>
      </div>

      {(act.isError || remove.isError) && (
        <p role="alert" className="mt-2 text-sm text-red-600">
          {errorMessage(act.error ?? remove.error, 'That did not work.')}
          {(act.error ?? remove.error) instanceof ApiError ? '' : ''}
        </p>
      )}

      <div className="mt-3">
        {deployments.isPending ? (
          <LoadingState label="Loading deployments…" />
        ) : deployments.isError && !deployments.data ? (
          <ErrorState
            title="Could not load deployments"
            onRetry={() => void deployments.refetch()}
          />
        ) : deployments.data.length === 0 ? (
          <p className="text-sm text-slate-500">Nothing has been deployed yet.</p>
        ) : (
          <ul
            aria-label={`Deployments of ${environment.name}`}
            className="divide-y divide-slate-200 dark:divide-slate-800"
          >
            {deployments.data.map((d) => (
              <li
                key={d.id}
                className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm"
              >
                <div className="min-w-0">
                  <span className="flex flex-wrap items-center gap-2">
                    <Badge tone={TONE[d.status]}>{LABEL[d.status]}</Badge>
                    <span className="font-mono text-xs">{d.commitSha?.slice(0, 7) ?? d.ref}</span>
                    <span className="text-slate-500">
                      {d.trigger === 'Manual'
                        ? 'Deployed'
                        : d.trigger === 'Redeploy'
                          ? 'Redeployed'
                          : 'Rolled back'}{' '}
                      by {d.requestedByName} · {new Date(d.createdAt).toLocaleString()}
                    </span>
                    {d.isCurrent && <Badge tone="green">Current</Badge>}
                    {d.revertedToPrevious && <Badge tone="amber">Reverted</Badge>}
                  </span>
                  {d.failureReason && (
                    <p className="mt-1 text-xs text-red-600">{d.failureReason}</p>
                  )}
                </div>
                <div className="flex gap-1">
                  {!isClient && (
                    <Button
                      variant="ghost"
                      onClick={() => setLogFor(d)}
                      aria-label={`View log of deployment ${d.commitSha?.slice(0, 7) ?? d.id.slice(0, 7)}`}
                    >
                      Log
                    </Button>
                  )}
                  {canDeploy && ACTIVE.includes(d.status) && (
                    <Button
                      variant="ghost"
                      disabled={!online}
                      onClick={() => act.mutate({ path: `deployments/${d.id}/cancel` })}
                    >
                      Cancel
                    </Button>
                  )}
                  {canDeploy && d.canRollback && (
                    <Button
                      variant="ghost"
                      disabled={!online || Boolean(active)}
                      onClick={() => act.mutate({ path: `deployments/${d.id}/rollback` })}
                    >
                      Roll back
                    </Button>
                  )}
                  {canDeploy && d.commitSha && !ACTIVE.includes(d.status) && (
                    <Button
                      variant="ghost"
                      disabled={!online || Boolean(active)}
                      onClick={() => act.mutate({ path: `deployments/${d.id}/redeploy` })}
                    >
                      Redeploy
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>

      {logFor && (
        <LogDialog
          project={project}
          environment={environment}
          deployment={logFor}
          onClose={() => setLogFor(null)}
        />
      )}
    </Card>
  )
}

function LogDialog({
  project,
  environment,
  deployment,
  onClose,
}: {
  project: Project
  environment: DeploymentEnvironment
  deployment: Deployment
  onClose: () => void
}) {
  const [lines, setLines] = useState<DeploymentLogs['lines']>([])
  const [complete, setComplete] = useState(false)
  const [failed, setFailed] = useState(false)
  const after = useRef(0)
  const end = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false
    let timer: number | undefined
    const load = async () => {
      try {
        const page = await apiGet<DeploymentLogs>(
          `/api/projects/${project.id}/environments/${environment.id}/deployments/${deployment.id}/logs?after=${after.current}`,
        )
        if (cancelled) return
        after.current = page.next
        setFailed(false)
        if (page.lines.length > 0) setLines((existing) => [...existing, ...page.lines])
        setComplete(page.complete)
        if (!page.complete) timer = window.setTimeout(() => void load(), 1500)
      } catch {
        if (cancelled) return
        setFailed(true)
        timer = window.setTimeout(() => void load(), 3000)
      }
    }
    void load()
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [project.id, environment.id, deployment.id])

  useEffect(() => {
    end.current?.scrollIntoView?.({ block: 'end' })
  }, [lines.length])

  return (
    <Dialog
      title={`Log · ${deployment.commitSha?.slice(0, 7) ?? deployment.ref}`}
      onClose={onClose}
    >
      <div
        role="log"
        aria-live="polite"
        className="max-h-96 overflow-auto rounded-md bg-slate-950 p-3 font-mono text-xs text-slate-100"
      >
        {lines.length === 0 && !failed && <p className="text-slate-400">Waiting for output…</p>}
        {lines.map((line) => (
          <p
            key={line.id}
            className={
              line.channel === 'Stderr'
                ? 'text-red-300'
                : line.channel === 'System'
                  ? 'text-sky-300'
                  : undefined
            }
          >
            {line.message}
          </p>
        ))}
        <div ref={end} />
      </div>
      <p className="mt-2 text-xs text-slate-500">
        {failed
          ? 'Could not refresh the log. Retrying…'
          : complete
            ? 'End of log.'
            : 'Live: updating while the deployment runs.'}
      </p>
    </Dialog>
  )
}

function EnvironmentDialog({
  project,
  environment,
  onClose,
}: {
  project: Project
  environment: DeploymentEnvironment | null
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(environment?.name ?? 'production')
  const [repositoryUrl, setRepositoryUrl] = useState(environment?.repositoryUrl ?? '')
  const [branch, setBranch] = useState(environment?.branch ?? 'main')
  const [buildCommand, setBuildCommand] = useState(
    environment?.buildCommand ?? 'npm ci && npm run build',
  )
  const [outputDirectory, setOutputDirectory] = useState(environment?.outputDirectory ?? 'dist')
  const [healthPath, setHealthPath] = useState(environment?.healthPath ?? '/')
  const [spaFallback, setSpaFallback] = useState(environment?.spaFallback ?? false)
  const [accessToken, setAccessToken] = useState('')
  const [clearToken, setClearToken] = useState(false)

  const base = `/api/projects/${project.id}/environments`
  const save = useMutation({
    mutationFn: () =>
      apiRequest(environment ? `${base}/${environment.id}` : base, {
        method: environment ? 'PUT' : 'POST',
        version: environment?.version,
        idempotencyKey: crypto.randomUUID(),
        body: {
          name: name.trim(),
          repositoryUrl: repositoryUrl.trim(),
          branch: branch.trim(),
          buildCommand: buildCommand.trim(),
          outputDirectory: outputDirectory.trim(),
          healthPath: healthPath.trim(),
          spaFallback,
          accessToken: accessToken || null,
          clearAccessToken: clearToken,
        },
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: keys.environments(project.id) })
      onClose()
    },
  })

  const errors = save.error instanceof ApiError ? save.error.fieldErrors : {}
  const submit = (event: FormEvent) => {
    event.preventDefault()
    save.mutate()
  }

  return (
    <Dialog title={environment ? 'Edit environment' : 'Add environment'} onClose={onClose}>
      <form onSubmit={submit} className="space-y-3">
        <TextInput
          label="Environment name"
          value={name}
          maxLength={100}
          onChange={(e) => setName(e.target.value)}
          error={errors.name?.[0]}
          required
        />
        <TextInput
          label="Repository URL (https)"
          value={repositoryUrl}
          maxLength={500}
          onChange={(e) => setRepositoryUrl(e.target.value)}
          error={errors.repositoryUrl?.[0]}
          placeholder="https://github.com/your-org/your-site.git"
          required
        />
        <TextInput
          label="Branch"
          value={branch}
          onChange={(e) => setBranch(e.target.value)}
          error={errors.branch?.[0]}
        />
        <TextInput
          label="Build command"
          value={buildCommand}
          onChange={(e) => setBuildCommand(e.target.value)}
          error={errors.buildCommand?.[0]}
        />
        <TextInput
          label="Output folder"
          value={outputDirectory}
          onChange={(e) => setOutputDirectory(e.target.value)}
          error={errors.outputDirectory?.[0]}
        />
        <TextInput
          label="Health check path"
          value={healthPath}
          onChange={(e) => setHealthPath(e.target.value)}
          error={errors.healthPath?.[0]}
        />
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={spaFallback}
            onChange={(e) => setSpaFallback(e.target.checked)}
          />
          Single-page app (serve index.html for unknown paths)
        </label>
        <TextInput
          label={
            environment?.hasAccessToken
              ? 'Access token (leave empty to keep the saved one)'
              : 'Access token for a private repository (optional)'
          }
          type="password"
          autoComplete="off"
          value={accessToken}
          maxLength={500}
          onChange={(e) => setAccessToken(e.target.value)}
          error={errors.accessToken?.[0]}
        />
        {environment?.hasAccessToken && (
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={clearToken}
              onChange={(e) => setClearToken(e.target.checked)}
            />
            Remove the saved access token
          </label>
        )}
        {save.isError && (
          <p role="alert" className="text-sm text-red-600">
            {save.error instanceof ApiError && save.error.status === 409
              ? (save.error.problem?.title ?? 'Conflict.')
              : errorMessage(save.error, 'Could not save.')}
          </p>
        )}
        <div className="flex justify-end gap-2 pt-2">
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" disabled={save.isPending}>
            {save.isPending ? 'Saving…' : 'Save environment'}
          </Button>
        </div>
      </form>
    </Dialog>
  )
}
