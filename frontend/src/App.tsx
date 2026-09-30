import { Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthProvider'
import { AppShell } from './components/AppShell'
import { Button, ErrorState, LoadingState } from './components/ui'
import { OutboxProvider } from './offline/OutboxProvider'
import { AcceptInvitePage, LoginPage, RegisterPage } from './pages/AuthPages'
import { ProjectPage } from './pages/ProjectPage'
import { ProjectsPage } from './pages/ProjectsPage'
import { TeamPage } from './pages/TeamPage'
import { RealtimeProvider } from './realtime/RealtimeProvider'

function RequireAuth() {
  const { me, loading, unreachable, retry } = useAuth()

  if (loading) return <LoadingState label="Loading…" />
  if (unreachable) {
    return (
      <div className="mx-auto max-w-md p-6">
        <ErrorState
          title="You are offline"
          message="We could not reach the server and have no saved session on this device. Reconnect to continue."
          onRetry={retry}
        />
      </div>
    )
  }
  if (!me) return <Navigate to="/login" replace />

  return (
    <OutboxProvider userId={me.userId}>
      <RealtimeProvider>
        <Outlet />
      </RealtimeProvider>
    </OutboxProvider>
  )
}

function NotFound() {
  return (
    <div className="mx-auto max-w-md space-y-3 p-6">
      <ErrorState title="Page not found" message="That address does not exist." />
      <Button onClick={() => window.location.assign('/')}>Go to projects</Button>
    </div>
  )
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/accept-invite" element={<AcceptInvitePage />} />
      <Route element={<RequireAuth />}>
        <Route element={<AppShell />}>
          <Route index element={<ProjectsPage />} />
          <Route path="projects/:projectId" element={<ProjectPage />} />
          <Route path="team" element={<TeamPage />} />
        </Route>
      </Route>
      <Route path="*" element={<NotFound />} />
    </Routes>
  )
}
