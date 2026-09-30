import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError, NetworkError } from '../api/http'
import { useAuth } from '../auth/AuthProvider'
import { Button, Card, LoadingState, TextInput } from '../components/ui'

function AuthLayout({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="mx-auto max-w-md p-6">
      <h1 className="mb-1 text-2xl font-semibold">Asset &amp; Handover Portal</h1>
      <p className="mb-4 text-slate-500">{title}</p>
      <Card>{children}</Card>
    </div>
  )
}

function useSubmit(action: () => Promise<void>) {
  const [error, setError] = useState<ApiError | string | null>(null)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await action()
      void navigate('/', { replace: true })
    } catch (e) {
      if (e instanceof NetworkError)
        setError('You appear to be offline. Connect to the internet and try again.')
      else if (e instanceof ApiError) setError(e)
      else throw e
    } finally {
      setBusy(false)
    }
  }
  return { error, busy, onSubmit }
}

function FormError({ error }: { error: ApiError | string | null }) {
  if (!error) return null
  const message = typeof error === 'string' ? error : (error.problem?.title ?? error.message)
  return (
    <p
      role="alert"
      className="rounded-md bg-red-50 p-2 text-sm text-red-700 dark:bg-red-950 dark:text-red-200"
    >
      {message}
    </p>
  )
}

function fieldError(error: ApiError | string | null, field: string): string | undefined {
  return typeof error === 'object' && error ? error.fieldErrors[field]?.[0] : undefined
}

export function LoginPage() {
  const { me, loading, signIn } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const { error, busy, onSubmit } = useSubmit(() => signIn(email, password))

  if (loading) return <LoadingState />
  if (me) return <Navigate to="/" replace />

  return (
    <AuthLayout title="Sign in to your workspace">
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-3">
        <FormError error={error} />
        <TextInput
          label="Email"
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
        />
        <TextInput
          label="Password"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <Button type="submit" disabled={busy} className="w-full">
          {busy ? 'Signing in…' : 'Sign in'}
        </Button>
        <p className="text-sm text-slate-500">
          New agency?{' '}
          <Link to="/register" className="text-indigo-600 underline">
            Create a workspace
          </Link>
        </p>
      </form>
    </AuthLayout>
  )
}

export function RegisterPage() {
  const { me, loading, register } = useAuth()
  const [organizationName, setOrganizationName] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const { error, busy, onSubmit } = useSubmit(() =>
    register({ organizationName, displayName, email, password }),
  )

  if (loading) return <LoadingState />
  if (me) return <Navigate to="/" replace />

  return (
    <AuthLayout title="Create your agency workspace">
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-3">
        <FormError error={error} />
        <TextInput
          label="Agency name"
          required
          value={organizationName}
          onChange={(e) => setOrganizationName(e.target.value)}
          error={fieldError(error, 'organizationName')}
        />
        <TextInput
          label="Your name"
          required
          autoComplete="name"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          error={fieldError(error, 'displayName')}
        />
        <TextInput
          label="Email"
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          error={fieldError(error, 'email')}
        />
        <TextInput
          label="Password (12+ characters)"
          type="password"
          autoComplete="new-password"
          required
          minLength={12}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={fieldError(error, 'password')}
        />
        <Button type="submit" disabled={busy} className="w-full">
          {busy ? 'Creating…' : 'Create workspace'}
        </Button>
        <p className="text-sm text-slate-500">
          Already have an account?{' '}
          <Link to="/login" className="text-indigo-600 underline">
            Sign in
          </Link>
        </p>
      </form>
    </AuthLayout>
  )
}

export function AcceptInvitePage() {
  const { me, loading, acceptInvitation } = useAuth()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const { error, busy, onSubmit } = useSubmit(() =>
    acceptInvitation({ token, displayName, password }),
  )

  if (loading) return <LoadingState />
  if (me) return <Navigate to="/" replace />

  return (
    <AuthLayout title="Accept your invitation">
      <form onSubmit={(e) => void onSubmit(e)} className="space-y-3">
        {!token && <FormError error="This invitation link is incomplete." />}
        <FormError error={error} />
        <TextInput
          label="Your name"
          required
          autoComplete="name"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          error={fieldError(error, 'displayName')}
        />
        <TextInput
          label="Choose a password (12+ characters)"
          type="password"
          autoComplete="new-password"
          required
          minLength={12}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={fieldError(error, 'password')}
        />
        <Button type="submit" disabled={busy || !token} className="w-full">
          {busy ? 'Joining…' : 'Join workspace'}
        </Button>
      </form>
    </AuthLayout>
  )
}
