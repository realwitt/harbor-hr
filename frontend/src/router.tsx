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
import { meQuery, queryClient } from './query'
import { AdminPage } from './routes/admin'
import { DeductionsPage } from './routes/deductions'
import { HomePage } from './routes/home'
import { InvitePage } from './routes/invite'
import { LeavePage } from './routes/leave'
import { LeaveNewPage } from './routes/leave-new'
import { SecurityPage } from './routes/security'
import { SignInPage } from './routes/sign-in'
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

const signInRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/sign-in',
  beforeLoad: async () => {
    try {
      const me = await queryClient.fetchQuery(meQuery)
      throw redirect({ to: me.ready ? '/' : '/security' })
    } catch (error) {
      if (isRedirect(error)) {
        throw error
      }
    }
  },
  component: SignInPage,
})

const authRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'auth',
  beforeLoad: async () => {
    try {
      await queryClient.fetchQuery(meQuery)
    } catch (error) {
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
        throw redirect({ to: '/sign-in' })
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

const adminRoute = createRoute({
  getParentRoute: () => readyRoute,
  path: '/admin',
  beforeLoad: async () => {
    const me = await queryClient.fetchQuery(meQuery)
    if (me.role !== 'hr_admin') {
      throw redirect({ to: '/' })
    }
  },
  component: AdminPage,
})

const routeTree = rootRoute.addChildren([
  inviteRoute,
  signInRoute,
  authRoute.addChildren([
    securityRoute,
    readyRoute.addChildren([homeRoute, leaveRoute, leaveNewRoute, deductionsRoute, teamRoute, adminRoute]),
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
