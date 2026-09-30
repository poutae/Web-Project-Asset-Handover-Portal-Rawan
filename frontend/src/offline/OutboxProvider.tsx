import { useQueryClient } from '@tanstack/react-query'
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { Outbox } from './outbox'
import { sendEntry } from './send'
import { indexedDbOutboxStorage } from './storage'
import { getTabId } from './tabId'
import type { NewOutboxEntry, OutboxEntry } from './types'

interface OutboxContextValue {
  entries: OutboxEntry[]
  /** The browser reports a connection and the server answered the last time we tried. */
  online: boolean
  syncing: boolean
  sessionExpired: boolean
  mutate: (input: NewOutboxEntry) => Promise<void>
  keepMine: (entry: OutboxEntry) => Promise<void>
  discard: (entry: OutboxEntry) => Promise<void>
  retry: (entry: OutboxEntry) => Promise<void>
}

const OutboxContext = createContext<OutboxContextValue | null>(null)

function useBrowserOnline(): boolean {
  const [online, setOnline] = useState(() => navigator.onLine)
  useEffect(() => {
    const on = () => setOnline(true)
    const off = () => setOnline(false)
    window.addEventListener('online', on)
    window.addEventListener('offline', off)
    return () => {
      window.removeEventListener('online', on)
      window.removeEventListener('offline', off)
    }
  }, [])
  return online
}

const RETRY_INTERVAL_MS = 3000

export function OutboxProvider({ userId, children }: { userId: string; children: ReactNode }) {
  const queryClient = useQueryClient()
  const browserOnline = useBrowserOnline()
  const [entries, setEntries] = useState<OutboxEntry[]>([])
  const [syncing, setSyncing] = useState(false)
  const [serverReachable, setServerReachable] = useState(true)
  const [sessionExpired, setSessionExpired] = useState(false)

  const outbox = useMemo(() => {
    const instance: Outbox = new Outbox({
      storage: indexedDbOutboxStorage,
      send: sendEntry,
      tabId: getTabId,
      userId: () => userId,
      locks: typeof navigator !== 'undefined' ? navigator.locks : undefined,
      onChange: () => {
        void instance.list().then(setEntries)
      },
      onSynced: (entry) => {
        for (const key of entry.invalidate) void queryClient.invalidateQueries({ queryKey: key })
      },
    })
    return instance
  }, [userId, queryClient])

  const run = useCallback(async () => {
    setSyncing(true)
    try {
      const outcome = await outbox.process()
      if (outcome === 'stopped-offline') setServerReachable(false)
      else if (outcome !== 'idle') setServerReachable(true)
      setSessionExpired(outcome === 'stopped-unauthorized')
    } finally {
      setSyncing(false)
    }
  }, [outbox])

  useEffect(() => {
    void outbox.list().then(setEntries)
    void run()
  }, [outbox, run])

  useEffect(() => {
    if (browserOnline) void run()
  }, [browserOnline, run])

  // Retry while anything is queued, and keep looking for entries left behind by closed tabs.
  useEffect(() => {
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible' && browserOnline) void run()
    }, RETRY_INTERVAL_MS)
    return () => window.clearInterval(timer)
  }, [browserOnline, run])

  const value = useMemo<OutboxContextValue>(
    () => ({
      entries,
      online: browserOnline && (serverReachable || entries.length === 0),
      syncing,
      sessionExpired,
      mutate: async (input) => {
        await outbox.enqueue(input)
        void run()
      },
      keepMine: async (entry) => {
        await outbox.keepMine(entry)
        void run()
      },
      discard: async (entry) => {
        await outbox.discard(entry.seq!)
        for (const key of entry.invalidate) void queryClient.invalidateQueries({ queryKey: key })
      },
      retry: async (entry) => {
        await outbox.retry(entry.seq!)
        void run()
      },
    }),
    [entries, browserOnline, serverReachable, syncing, sessionExpired, outbox, run, queryClient],
  )

  return <OutboxContext.Provider value={value}>{children}</OutboxContext.Provider>
}

export function useOutbox(): OutboxContextValue {
  const value = useContext(OutboxContext)
  if (!value) throw new Error('useOutbox must be used inside <OutboxProvider>')
  return value
}
