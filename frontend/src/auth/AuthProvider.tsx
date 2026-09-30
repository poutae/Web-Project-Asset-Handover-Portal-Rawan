import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useMemo, type ReactNode } from 'react'
import { ApiError, NetworkError, apiRequest, resetCsrfToken } from '../api/http'
import { keys } from '../api/queries'
import type { Me } from '../api/types'
import { clearPersistedCache } from '../offline/queryPersister'

interface AuthContextValue {
  /** Null when signed out. */
  me: Me | null
  loading: boolean
  /** True when the user could not be checked because the server is unreachable. */
  unreachable: boolean
  retry: () => void
  signIn: (email: string, password: string) => Promise<void>
  register: (input: {
    organizationName: string
    displayName: string
    email: string
    password: string
  }) => Promise<void>
  acceptInvitation: (input: {
    token: string
    displayName: string
    password: string
  }) => Promise<void>
  signOut: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

async function fetchMe(signal: AbortSignal): Promise<Me | null> {
  try {
    const { data } = await apiRequest<Me>('/api/auth/me', { signal })
    return data
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return null
    throw error
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const meQuery = useQuery({
    queryKey: keys.me,
    queryFn: ({ signal }) => fetchMe(signal),
    retry: (count, error) => !(error instanceof NetworkError) && count < 1,
    networkMode: 'offlineFirst',
    staleTime: 5 * 60_000,
  })

  const establish = useCallback(
    async (me: Me) => {
      // Cached data belongs to whoever was signed in before; and CSRF tokens are bound to the identity.
      resetCsrfToken()
      await clearPersistedCache()
      queryClient.clear()
      queryClient.setQueryData(keys.me, me)
    },
    [queryClient],
  )

  const signInMutation = useMutation({
    mutationFn: async (input: { email: string; password: string }) =>
      (await apiRequest<Me>('/api/auth/login', { method: 'POST', body: input })).data as Me,
    onSuccess: establish,
  })
  const registerMutation = useMutation({
    mutationFn: async (input: {
      organizationName: string
      displayName: string
      email: string
      password: string
    }) => (await apiRequest<Me>('/api/auth/register', { method: 'POST', body: input })).data as Me,
    onSuccess: establish,
  })
  const acceptMutation = useMutation({
    mutationFn: async (input: { token: string; displayName: string; password: string }) =>
      (await apiRequest<Me>('/api/invitations/accept', { method: 'POST', body: input })).data as Me,
    onSuccess: establish,
  })

  const signOut = useCallback(async () => {
    try {
      await apiRequest('/api/auth/logout', { method: 'POST' })
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 401)) throw error
    }
    resetCsrfToken()
    await clearPersistedCache()
    queryClient.clear()
    queryClient.setQueryData(keys.me, null)
  }, [queryClient])

  const value = useMemo<AuthContextValue>(
    () => ({
      me: meQuery.data ?? null,
      loading: meQuery.isPending,
      unreachable: meQuery.isError && meQuery.data === undefined,
      retry: () => void meQuery.refetch(),
      signIn: async (email, password) =>
        void (await signInMutation.mutateAsync({ email, password })),
      register: async (input) => void (await registerMutation.mutateAsync(input)),
      acceptInvitation: async (input) => void (await acceptMutation.mutateAsync(input)),
      signOut,
    }),
    [meQuery, signInMutation, registerMutation, acceptMutation, signOut],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>')
  return value
}
