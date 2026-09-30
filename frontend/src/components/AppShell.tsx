import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { useOutbox } from '../offline/OutboxProvider'
import { useRealtimeStatus } from '../realtime/RealtimeProvider'
import { ConflictDialog } from './ConflictDialog'
import { SyncStatus } from './SyncStatus'
import { Button } from './ui'

const REALTIME_LABEL = {
  connected: 'Live',
  connecting: 'Connecting…',
  reconnecting: 'Reconnecting…',
  disconnected: 'Not live',
} as const

export function AppShell() {
  const { me, signOut } = useAuth()
  const { online } = useOutbox()
  const realtime = useRealtimeStatus()
  if (!me) return null

  const link = ({ isActive }: { isActive: boolean }) =>
    `rounded-md px-2 py-1 text-sm font-medium ${isActive ? 'bg-slate-200 dark:bg-slate-800' : 'hover:bg-slate-100 dark:hover:bg-slate-800'}`

  return (
    <div className="min-h-screen">
      {!online && (
        <div
          role="status"
          data-testid="offline-banner"
          className="bg-amber-100 px-4 py-2 text-center text-sm text-amber-900"
        >
          You are offline. Changes you make are saved on this device and will sync when you are back
          online.
        </div>
      )}
      <header className="border-b border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-3 px-4 py-3">
          <nav className="flex items-center gap-2" aria-label="Main">
            <span className="mr-2 font-semibold">{me.organization.name}</span>
            <NavLink to="/" end className={link}>
              Projects
            </NavLink>
            {me.role === 'Admin' && (
              <NavLink to="/team" className={link}>
                Team
              </NavLink>
            )}
          </nav>
          <div className="flex items-center gap-3">
            <span
              role="status"
              aria-label={`Realtime: ${realtime}`}
              data-testid="realtime-status"
              data-state={realtime}
              className="text-xs text-slate-500"
            >
              <span
                aria-hidden="true"
                className={`mr-1 inline-block h-2 w-2 rounded-full ${realtime === 'connected' ? 'bg-green-500' : 'bg-slate-400'}`}
              />
              {REALTIME_LABEL[realtime]}
            </span>
            <SyncStatus />
            <span className="text-sm text-slate-500">{me.displayName}</span>
            <Button variant="secondary" onClick={() => void signOut()}>
              Sign out
            </Button>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-5xl p-4">
        <Outlet />
      </main>
      <ConflictDialog />
    </div>
  )
}
