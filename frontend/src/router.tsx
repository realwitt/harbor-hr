import { QueryClientProvider } from '@tanstack/react-query'
import {
  Outlet,
  RouterProvider,
  createRootRoute,
  createRoute,
  createRouter,
  isRedirect,
  redirect,
} from '@tanstack/react-router'
import { ApiError } from './api'
import { safeJoinReturn } from './join-return'
import { meQuery, queryClient } from './query'
import { AuditPage } from './routes/admin/audit'
import { CalendarPage } from './routes/admin/calendar'
import { CapsPage } from './routes/admin/caps'
import { InvitesPage } from './routes/admin/invites'
import { JoinRequestPage, JoinRequestsPage } from './routes/admin/join-requests'
import { LedgerPage } from './routes/admin/ledger'
import { LeaveTypesPage } from './routes/admin/leave-types'
import { PayPeriodsPage } from './routes/admin/pay-periods'
import { PeoplePage } from './routes/admin/people'
import { DeductionsPage } from './routes/deductions'
import { HomePage } from './routes/home'
import { InvitePage } from './routes/invite'
import { JoinPage } from './routes/join'
import { LeavePage } from './routes/leave'
import { LeaveNewPage } from './routes/leave-new'
import { SecurityPage } from './routes/security'
import { safeAuthorizeNext } from './authorize-next'
import { ConnectAiPage } from './routes/connect-ai'
import { RecoverySignInPage, SignInPage } from './routes/sign-in'
import { TeamPage } from './routes/team'
import { Shell } from './shell'
import { ErrorText } from './ui'

const rootRoute = createRootRoute({
  component: () => <Outlet />,
  notFoundComponent: () => <p className="p-3 text-sm">That page is missing.</p>,
})

const inviteRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/invite/$token',
  component: InvitePage,
})

async function redirectIfSignedIn(next: string): Promise<void> {
  try {
    const me = await queryClient.fetchQuery(meQuery)
    const authorize = safeAuthorizeNext(next)
    if (authorize && typeof window !== 'undefined') {
      window.location.replace(authorize)
      return new Promise(() => {})
    }

    throw redirect({ to: me.ready ? '/' : '/security' })
  } catch (error) {
    if (isRedirect(error)) {
      throw error
    }
  }
}

const connectAiRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/connect-ai',
  component: ConnectAiPage,
})

const signInRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/sign-in',
  validateSearch: (search: Record<string, unknown>): { next: string; returnTo: string } => ({
    next: typeof search.next === 'string' ? search.next : '',
    returnTo: safeJoinReturn(typeof search.returnTo === 'string' ? search.returnTo : ''),
  }),
  beforeLoad: ({ search }) => redirectIfSignedIn(search.next),
  component: SignInPage,
})

const recoverySignInRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/sign-in/recovery',
  validateSearch: (search: Record<string, unknown>): { email: string; next: string; returnTo: string } => ({
    email: typeof search.email === 'string' ? search.email : '',
    next: typeof search.next === 'string' ? search.next : '',
    returnTo: safeJoinReturn(typeof search.returnTo === 'string' ? search.returnTo : ''),
  }),
  beforeLoad: ({ search }) => redirectIfSignedIn(search.next),
  component: RecoverySignInPage,
})

const authRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'auth',
  beforeLoad: async ({ location }) => {
    try {
      await queryClient.fetchQuery(meQuery)
    } catch (error) {
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
        throw redirect({
          to: '/sign-in',
          search: { next: '', returnTo: safeJoinReturn(location.pathname) },
        })
      }

      throw error
    }
  },
  component: Shell,
})

const securityRoute = createRoute({
  getParentRoute: () => authRoute,
  path: '/security',
  component: SecurityPage,
})

const readyRoute = createRoute({
  getParentRoute: () => authRoute,
  id: 'ready',
  beforeLoad: async () => {
    const me = await queryClient.fetchQuery(meQuery)
    if (!me.ready) {
      throw redirect({ to: '/security' })
    }
  },
  component: () => <Outlet />,
})

const homeRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/',
  component: HomePage,
})

const leaveRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/leave',
  component: LeavePage,
})

const leaveNewRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/leave/new',
  component: LeaveNewPage,
})

const deductionsRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/deductions',
  component: DeductionsPage,
})

const teamRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/team',
  component: TeamPage,
})

async function requireAdmin(): Promise<void> {
  const me = await queryClient.fetchQuery(meQuery)
  if (me.role !== 'hr_admin') {
    throw redirect({ to: '/' })
  }
}

const adminIndexRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin',
  beforeLoad: async () => {
    await requireAdmin()
    throw redirect({ to: '/admin/people' })
  },
  component: () => null,
})

const adminPeopleRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/people',
  beforeLoad: requireAdmin,
  component: PeoplePage,
})

const adminInvitesRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/invites',
  beforeLoad: requireAdmin,
  component: InvitesPage,
})

const adminJoinRequestsRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/join-requests',
  beforeLoad: requireAdmin,
  component: JoinRequestsPage,
})

const adminJoinRequestRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/join-requests/$id',
  beforeLoad: requireAdmin,
  component: JoinRequestPage,
})

const joinRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/join',
  component: JoinPage,
})

const adminLeaveTypesRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/leave-types',
  beforeLoad: requireAdmin,
  component: LeaveTypesPage,
})

const adminCalendarRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/calendar',
  beforeLoad: requireAdmin,
  component: CalendarPage,
})

const adminLedgerRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/ledger',
  beforeLoad: requireAdmin,
  component: LedgerPage,
})

const adminPayRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/pay-periods',
  beforeLoad: requireAdmin,
  component: PayPeriodsPage,
})

const adminCapsRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/caps',
  beforeLoad: requireAdmin,
  component: CapsPage,
})

const adminAuditRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin/audit',
  beforeLoad: requireAdmin,
  component: AuditPage,
})

const routeTree = rootRoute.addChildren([
  inviteRoute,
  connectAiRoute,
  signInRoute,
  recoverySignInRoute,
  joinRoute,
  authRoute.addChildren([
    securityRoute,
    readyRoute.addChildren([
      homeRoute,
      leaveRoute,
      leaveNewRoute,
      deductionsRoute,
      teamRoute,
      adminIndexRoute,
      adminPeopleRoute,
      adminInvitesRoute,
      adminJoinRequestsRoute,
      adminJoinRequestRoute,
      adminLeaveTypesRoute,
      adminCalendarRoute,
      adminLedgerRoute,
      adminPayRoute,
      adminCapsRoute,
      adminAuditRoute,
    ]),
  ]),
])

const router = createRouter({
  routeTree,
  defaultPreload: 'intent',
  defaultErrorComponent: ({ error }) => (
    <div className="p-3">
      <ErrorText error={error} />
    </div>
  ),
})

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}
