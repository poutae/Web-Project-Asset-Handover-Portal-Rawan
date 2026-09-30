import { QueryClient } from '@tanstack/react-query'
import { PersistQueryClientProvider } from '@tanstack/react-query-persist-client'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { App } from './App'
import { NetworkError } from './api/http'
import { AuthProvider } from './auth/AuthProvider'
import { indexedDbPersister } from './offline/queryPersister'
import { initTabIdentity } from './offline/tabId'
import './index.css'

const WEEK = 7 * 24 * 60 * 60 * 1000

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Serve cached data while offline instead of pausing forever; failures then surface as errors.
      networkMode: 'offlineFirst',
      staleTime: 30_000,
      gcTime: WEEK,
      refetchOnReconnect: 'always',
      retry: (count, error) => !(error instanceof NetworkError) && count < 2,
    },
  },
})

async function start() {
  await initTabIdentity()

  if ('serviceWorker' in navigator && import.meta.env.PROD) {
    void navigator.serviceWorker.register('/sw.js')
  }

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <PersistQueryClientProvider
        client={queryClient}
        persistOptions={{
          persister: indexedDbPersister,
          maxAge: WEEK,
          buster: 'portal-cache-1',
          dehydrateOptions: { shouldDehydrateQuery: (query) => query.state.status === 'success' },
        }}
      >
        <BrowserRouter>
          <AuthProvider>
            <App />
          </AuthProvider>
        </BrowserRouter>
      </PersistQueryClientProvider>
    </StrictMode>,
  )
}

void start()
