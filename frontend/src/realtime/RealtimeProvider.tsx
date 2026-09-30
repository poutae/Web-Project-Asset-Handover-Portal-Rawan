import { useQueryClient } from '@tanstack/react-query'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { keys } from '../api/queries'
import type { RealtimeChange } from '../api/types'
import { getTabId } from '../offline/tabId'

export type RealtimeStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

const RealtimeContext = createContext<RealtimeStatus>('connecting')

export function useRealtimeStatus(): RealtimeStatus {
  return useContext(RealtimeContext)
}

/**
 * Keeps one push connection open for the signed-in user. An event only says "this changed", so the
 * matching queries are re-fetched through the normal authorized API.
 */
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<RealtimeStatus>('connecting')

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/portal')
      .withAutomaticReconnect([0, 500, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build()

    const refreshAll = () => {
      void queryClient.invalidateQueries()
    }

    connection.on('changed', (change: RealtimeChange) => {
      // Changes made by this very tab are already refreshed when the outbox confirms them.
      if (change.originClientId === getTabId()) return
      const id = change.projectId
      switch (change.kind) {
        case 'project':
          void queryClient.invalidateQueries({ queryKey: keys.projects })
          void queryClient.invalidateQueries({ queryKey: keys.project(id) })
          break
        case 'member':
          void queryClient.invalidateQueries({ queryKey: keys.members(id) })
          void queryClient.invalidateQueries({ queryKey: keys.projects })
          void queryClient.invalidateQueries({ queryKey: keys.project(id) })
          break
        case 'milestone':
          void queryClient.invalidateQueries({ queryKey: keys.milestones(id) })
          break
        case 'note':
          void queryClient.invalidateQueries({ queryKey: keys.notes(id) })
          break
        case 'document':
          void queryClient.invalidateQueries({ queryKey: keys.documents(id) })
          break
        case 'environment':
        case 'deployment':
          void queryClient.invalidateQueries({ queryKey: keys.environments(id) })
          void queryClient.invalidateQueries({ queryKey: ['deployments', id] })
          void queryClient.invalidateQueries({ queryKey: ['deployment-logs'] })
          break
      }
    })
    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(() => {
      setStatus('connected')
      // Anything that changed while the connection was down was never pushed to us.
      refreshAll()
    })
    connection.onclose(() => setStatus('disconnected'))

    let cancelled = false
    let retryTimer: number | undefined
    const start = async () => {
      try {
        await connection.start()
        if (!cancelled) setStatus('connected')
      } catch {
        // Initial connect failed (offline / server down): keep trying, since automatic reconnect only
        // applies to connections that were established once.
        if (!cancelled) {
          setStatus('disconnected')
          retryTimer = window.setTimeout(() => void start(), 3000)
        }
      }
    }
    void start()

    const onOnline = () => {
      if (connection.state === HubConnectionState.Disconnected) void start()
    }
    window.addEventListener('online', onOnline)

    return () => {
      cancelled = true
      window.clearTimeout(retryTimer)
      window.removeEventListener('online', onOnline)
      void connection.stop()
    }
  }, [queryClient])

  return <RealtimeContext.Provider value={status}>{children}</RealtimeContext.Provider>
}
