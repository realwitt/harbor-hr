import { QueryClient } from '@tanstack/react-query'
import { api } from './api'
import type { Me } from './types'

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: false,
      staleTime: 15_000,
    },
  },
})

export const meQuery = {
  queryKey: ['me'] as const,
  queryFn: () => api<Me>('/api/auth/me'),
}
