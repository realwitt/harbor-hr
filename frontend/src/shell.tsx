import { useMutation, useQuery } from '@tanstack/react-query'
import { Link as RouterLink, Outlet, useNavigate, useRouterState } from '@tanstack/react-router'
import type { AnchorHTMLAttributes, ComponentProps, MouseEvent } from 'react'
import type { Key } from 'react-aria-components'
import { useState } from 'react'
import {
  Button as AriaButton,
  Dialog,
  Link as AriaLink,
  Menu,
  MenuItem,
  MenuTrigger,
  SubmenuTrigger,
  Modal,
  ModalOverlay,
  NavigationTree,
  NavigationTreeItem,
  NavigationTreeItemContent,
  Popover,
} from 'react-aria-components'
import { api } from './api'
import { meQuery, queryClient } from './query'
import { Button, ErrorText } from './ui'

type NavTo =
  | '/'
  | '/leave'
  | '/leave/new'
  | '/deductions'
  | '/team'
  | '/security'
  | '/admin/people'
  | '/admin/invites'
  | '/admin/leave-types'
  | '/admin/calendar'
  | '/admin/ledger'
  | '/admin/pay-periods'
  | '/admin/caps'
  | '/admin/audit'

const adminMenus: { label: string; items: { to: NavTo; label: string }[] }[] = [
  {
    label: 'People',
    items: [
      { to: '/admin/people', label: 'Directory' },
      { to: '/admin/invites', label: 'Invites' },
    ],
  },
  {
    label: 'Time',
    items: [
      { to: '/admin/leave-types', label: 'Leave types' },
      { to: '/admin/calendar', label: 'Calendar' },
      { to: '/admin/ledger', label: 'Ledger' },
    ],
  },
  {
    label: 'Pay',
    items: [
      { to: '/admin/pay-periods', label: 'Pay periods' },
      { to: '/admin/caps', label: 'Deduction caps' },
    ],
  },
]

function NavItem({ to, label, onNavigate }: { to: NavTo; label: string; onNavigate?: () => void }) {
  return (
    <NavigationTreeItem id={to} href={to} textValue={label}>
      <NavigationTreeItemContent>
        <AriaLink
          render={(domProps) => {
            const props = domProps as AnchorHTMLAttributes<HTMLAnchorElement>
            const { href: _href, children: _children, onClick, ...rest } = props
            return (
              <RouterLink
                to={to}
                {...(rest as Omit<ComponentProps<typeof RouterLink>, 'to'>)}
                onClick={(event: MouseEvent<HTMLAnchorElement>) => {
                  onClick?.(event)
                  onNavigate?.()
                }}
              >
                {label}
              </RouterLink>
            )
          }}
        >
          {label}
        </AriaLink>
      </NavigationTreeItemContent>
    </NavigationTreeItem>
  )
}

function MenuIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M4 7h16M4 12h16M4 17h16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  )
}

function CloseIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="M6 6l12 12M18 6 6 18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  )
}

function ChevronDown() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m6 9 6 6 6-6" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function ChevronRight() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path d="m9 18 6-6-6-6" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function PhoneAdmin({ pathname, onNavigate }: { pathname: string; onNavigate?: () => void }) {
  const [adminOpen, setAdminOpen] = useState(pathname.startsWith('/admin'))
  const [open, setOpen] = useState<string | null>(
    adminMenus.find((menu) => menu.items.some((item) => item.to === pathname))?.label ?? null,
  )

  return (
    <div className="admin-phone">
      <button
        type="button"
        className="admin-root"
        aria-expanded={adminOpen}
        data-current={pathname.startsWith('/admin') || undefined}
        onClick={() => setAdminOpen((value) => !value)}
      >
        <span>Admin</span>
        <ChevronDown />
      </button>
      {adminOpen ? (
        <div className="admin-sub">
          {adminMenus.map((menu) => {
            const current = menu.items.some((item) => item.to === pathname)
            const expanded = open === menu.label
            return (
              <div key={menu.label}>
                <button
                  type="button"
                  className="admin-sub-link admin-branch"
                  aria-expanded={expanded}
                  data-current={current || undefined}
                  onClick={() => setOpen(expanded ? null : menu.label)}
                >
                  <span>{menu.label}</span>
                  <ChevronRight />
                </button>
                {expanded ? (
                  <div className="admin-sub">
                    {menu.items.map((item) => (
                      <RouterLink
                        key={item.to}
                        to={item.to}
                        className="admin-sub-link"
                        data-current={pathname === item.to || undefined}
                        onClick={() => onNavigate?.()}
                      >
                        {item.label}
                      </RouterLink>
                    ))}
                  </div>
                ) : null}
              </div>
            )
          })}
          <RouterLink
            to="/admin/audit"
            className="admin-sub-link"
            data-current={pathname === '/admin/audit' || undefined}
            onClick={() => onNavigate?.()}
          >
            Audit
          </RouterLink>
        </div>
      ) : null}
    </div>
  )
}

function AdminMenu({ pathname, onNavigate }: { pathname: string; onNavigate?: () => void }) {
  const navigate = useNavigate()
  const leaves = [...adminMenus.flatMap((menu) => menu.items), { to: '/admin/audit' as const, label: 'Audit' }]
  const onPick = (key: Key) => {
    const target = leaves.find((item) => item.to === String(key))
    if (!target) {
      return
    }

    void navigate({ to: target.to })
    onNavigate?.()
  }

  return (
    <MenuTrigger>
      <AriaButton className="admin-root" data-current={pathname.startsWith('/admin') || undefined}>
        <span>Admin</span>
        <ChevronDown />
      </AriaButton>
      <Popover className="react-aria-Popover" placement="bottom start">
        <Menu className="admin-menu" onAction={onPick}>
          {adminMenus.map((menu) => {
            const current = menu.items.some((item) => item.to === pathname)
            return (
              <SubmenuTrigger key={menu.label}>
                <MenuItem textValue={menu.label} className="admin-item" data-current={current || undefined}>
                  {({ hasSubmenu }) => (
                    <>
                      <span>{menu.label}</span>
                      {hasSubmenu ? <ChevronRight /> : null}
                    </>
                  )}
                </MenuItem>
                <Popover className="react-aria-Popover" placement="right top" offset={0}>
                  <Menu className="admin-menu" onAction={onPick}>
                    {menu.items.map((item) => (
                      <MenuItem
                        key={item.to}
                        id={item.to}
                        textValue={item.label}
                        className="admin-item"
                        data-current={pathname === item.to || undefined}
                      >
                        {item.label}
                      </MenuItem>
                    ))}
                  </Menu>
                </Popover>
              </SubmenuTrigger>
            )
          })}
          <MenuItem id="/admin/audit" textValue="Audit" className="admin-item" data-current={pathname === '/admin/audit' || undefined}>
            Audit
          </MenuItem>
        </Menu>
      </Popover>
    </MenuTrigger>
  )
}

function NavLinks({
  items,
  pathname,
  name,
  signOutPending,
  onSignOut,
  onNavigate,
  showAdmin,
  adminVariant = 'flyout',
}: {
  items: { to: NavTo; label: string }[]
  pathname: string
  name: string
  signOutPending: boolean
  onSignOut: () => void
  onNavigate?: () => void
  showAdmin?: boolean
  adminVariant?: 'flyout' | 'sheet'
}) {
  return (
    <>
      <NavigationTree aria-label="Harbor" selectedRoute={pathname} className="react-aria-NavigationTree sidebar-nav">
        {items.map((item) => (
          <NavItem key={item.to} to={item.to} label={item.label} onNavigate={onNavigate} />
        ))}
      </NavigationTree>
      {showAdmin ? (
        adminVariant === 'sheet' ? (
          <PhoneAdmin pathname={pathname} onNavigate={onNavigate} />
        ) : (
          <AdminMenu pathname={pathname} onNavigate={onNavigate} />
        )
      ) : null}
      <div className="sidebar-foot">
        <span className="sidebar-name">{name}</span>
        <Button quiet onPress={onSignOut} isDisabled={signOutPending}>
          Sign out
        </Button>
      </div>
    </>
  )
}

export function Shell() {
  const navigate = useNavigate()
  const pathname = useRouterState({ select: (state) => state.location.pathname })
  const me = useQuery(meQuery)
  const [menuOpen, setMenuOpen] = useState(false)
  const signOut = useMutation({
    mutationFn: () => api('/api/auth/sign-out', { method: 'POST' }),
    onSuccess: async () => {
      queryClient.clear()
      await navigate({ to: '/sign-in' })
    },
  })

  if (!me.data) {
    return (
      <main className="guest">
        <ErrorText error={me.error} />
      </main>
    )
  }

  const person = me.data
  const items: { to: NavTo; label: string }[] = []
  if (person.ready) {
    items.push(
      { to: '/', label: 'Home' },
      { to: '/leave', label: 'Requests' },
      { to: '/leave/new', label: 'Request leave' },
      { to: '/deductions', label: 'Deductions' },
      { to: '/team', label: 'Team' },
    )
  }
  items.push({ to: '/security', label: 'Security' })
  const showAdmin = person.ready && person.role === 'hr_admin'

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <p className="sidebar-brand">
          <span className="sidebar-mark" aria-hidden="true">
            H
          </span>
          <span>Harbor</span>
        </p>
        <Button quiet label="Menu" onPress={() => setMenuOpen(true)}>
          <MenuIcon />
        </Button>
        <div className="sidebar-desktop">
          <NavLinks
            items={items}
            pathname={pathname}
            name={person.name}
            signOutPending={signOut.isPending}
            onSignOut={() => signOut.mutate()}
            showAdmin={showAdmin}
          />
        </div>
      </aside>
      <ModalOverlay
        className="react-aria-ModalOverlay nav-overlay"
        isOpen={menuOpen}
        onOpenChange={setMenuOpen}
        isDismissable
      >
        <Modal className="react-aria-Modal nav-sheet">
          <Dialog className="react-aria-Dialog nav-dialog" aria-label="Menu">
            <div className="nav-sheet-head">
              <p className="nav-sheet-brand">
                <span className="sidebar-mark" aria-hidden="true">
                  H
                </span>
                <span>Harbor</span>
              </p>
              <Button quiet label="Close" onPress={() => setMenuOpen(false)}>
                <CloseIcon />
              </Button>
            </div>
            <NavLinks
              items={items}
              pathname={pathname}
              name={person.name}
              signOutPending={signOut.isPending}
              onSignOut={() => signOut.mutate()}
              onNavigate={() => setMenuOpen(false)}
              showAdmin={showAdmin}
              adminVariant="sheet"
            />
          </Dialog>
        </Modal>
      </ModalOverlay>
      <main className="app-main">
        <div className="app-main-inner">
          {person.ready ? null : <p>Finish security setup before you use Harbor.</p>}
          <ErrorText error={signOut.error} />
          <Outlet />
        </div>
      </main>
    </div>
  )
}
