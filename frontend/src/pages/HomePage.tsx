import { useQuery } from '@tanstack/react-query'
import { apiGet } from '../lib/api'

interface Health {
  status: string
}

export function HomePage() {
  const health = useQuery({
    queryKey: ['health'],
    queryFn: ({ signal }) => apiGet<Health>('/api/health', signal),
  })

  return (
    <section className="space-y-2">
      <h1 className="text-2xl font-semibold">Asset &amp; Handover Portal</h1>
      {health.isPending && <p role="status">Checking API…</p>}
      {health.isError && (
        <p role="alert" className="text-red-600">
          The API is unreachable. Please try again shortly.
        </p>
      )}
      {health.isSuccess && <p role="status">API status: {health.data.status}</p>}
    </section>
  )
}
