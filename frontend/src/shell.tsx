import { useMutation, useQuery } from '@tanstack/react-query'
import { Link, Outlet, useNavigate } from '@tanstack/react-router'
import { api } from './api'
import { meQuery, queryClient } from './query'
import { Button, ErrorText } from './ui'

function NavLink({ to, label, exact }: { to: '/' | '/leave' | '/leave/new' | '/deductions' | '/team' | '/security' | '/admin'; label: string; exact?: boolean }) {
  return (
    <Link
      to={to}
      activeOptions={{ exact: exact ?? false }}
      className="text-sm text-neutral-700 data-[status=active]:font-semibold"
    >
      {label}
    </Link>
  )
}

export function Shell() {
  const navigate = useNavigate()
  const me = useQuery(meQuery)
  const signOut = useMutation({
    mutationFn: () => api('/api/auth/sign-out', { method: 'POST' }),
    onSuccess: async () => {
      queryClient.clear()
      await navigate({ to: '/sign-in' })
    },
  })

  if (!me.data) {
    return (
      <main className="p-3">
        <ErrorText error={me.error} />
      </main>
    )
  }

  const person = me.data

  return (
    <div className="min-h-screen max-w-full">
      <header className="flex w-full max-w-full flex-wrap items-center gap-x-3 gap-y-1 border-b border-neutral-200 bg-white px-3 py-1">
        <span className="text-sm font-semibold">Harbor</span>
        <nav className="flex min-w-0 flex-1 flex-wrap items-center gap-x-3 gap-y-1">
          {person.ready ? (
            <>
              <NavLink to="/" label="Home" exact />
              <NavLink to="/leave" label="Requests" />
              <NavLink to="/leave/new" label="Request leave" />
              <NavLink to="/deductions" label="Deductions" />
              <NavLink to="/team" label="Team" />
            </>
          ) : null}
          <NavLink to="/security" label="Security" />
          {person.ready && person.role === 'hr_admin' ? <NavLink to="/admin" label="Admin" /> : null}
        </nav>
        <div className="flex shrink-0 items-center gap-2">
          <span className="max-w-36 truncate text-sm">{person.name}</span>
          <Button quiet onPress={() => signOut.mutate()} isDisabled={signOut.isPending}>
            Sign out
          </Button>
        </div>
      </header>
      <main className="mx-auto flex max-w-5xl flex-col gap-2 px-3 py-3">
        {person.ready ? null : <p className="text-sm">Finish security setup before you use Harbor.</p>}
        <ErrorText error={signOut.error} />
        <Outlet />
      </main>
    </div>
  )
}
