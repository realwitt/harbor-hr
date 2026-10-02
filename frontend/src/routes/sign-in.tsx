import { useForm } from '@tanstack/react-form'
import { useMutation } from '@tanstack/react-query'
import { Link, getRouteApi, useNavigate } from '@tanstack/react-router'
import { safeAuthorizeNext } from '../authorize-next'
import { api } from '../api'
import { meQuery, queryClient } from '../query'
import type { Me } from '../types'
import { Button, ErrorText, Page, TextField, fieldErrors } from '../ui'
import { getPasskey } from '../webauthn'

const signInRoute = getRouteApi('/sign-in')
const recoveryRoute = getRouteApi('/sign-in/recovery')

function openNext(next: string): boolean {
  const target = safeAuthorizeNext(next)
  if (!target) {
    return false
  }

  window.location.assign(target)
  return true
}

async function loadMe(): Promise<Me> {
  const me = await api<Me>('/api/auth/me')
  queryClient.setQueryData(meQuery.queryKey, me)
  return me
}

function emailError(value: string): string | undefined {
  return value.includes('@') ? undefined : 'Enter an email address.'
}

export function SignInPage() {
  const navigate = useNavigate()
  const search = signInRoute.useSearch()
  const passkey = useMutation({
    mutationFn: async (email: string) => {
      const options = await api<unknown>('/api/auth/assert/options', {
        method: 'POST',
        body: JSON.stringify({ email }),
      })
      const assertion = await getPasskey(options)
      await api('/api/auth/assert', {
        method: 'POST',
        body: JSON.stringify({ email, assertion }),
      })
      return loadMe()
    },
    onSuccess: (me) => {
      if (openNext(search.next)) {
        return
      }

      void navigate({ to: me.ready ? '/' : '/security' })
    },
  })
  const form = useForm({
    defaultValues: { email: '' },
    onSubmit: ({ value }) => {
      passkey.mutate(value.email.trim().toLowerCase())
    },
  })

  return (
    <main className="guest">
      <div className="guest-card panel">
        <Page title="Sign in">
          <form
            className="flex flex-col gap-2"
            onSubmit={(event) => {
              event.preventDefault()
              void form.handleSubmit()
            }}
          >
            <form.Field
              name="email"
              validators={{
                onSubmit: ({ value }) => emailError(value),
              }}
            >
              {(field) => (
                <TextField
                  label="Email"
                  type="email"
                  autoComplete="username webauthn"
                  value={field.state.value}
                  onChange={field.handleChange}
                  onBlur={field.handleBlur}
                  error={fieldErrors(field.state.meta.errors)}
                />
              )}
            </form.Field>
            <Button type="submit" isDisabled={passkey.isPending}>
              Use passkey
            </Button>
            <ErrorText error={passkey.error} />
            <form.Subscribe selector={(state) => state.values.email}>
              {(email) => (
                <Link
                  className="trouble-link"
                  to="/sign-in/recovery"
                  search={{ email: email.trim(), next: search.next }}
                >
                  Having trouble signing in?
                </Link>
              )}
            </form.Subscribe>
            <Link className="trouble-link" to="/connect-ai">
              Connect with your AI
            </Link>
          </form>
        </Page>
      </div>
    </main>
  )
}

export function RecoverySignInPage() {
  const navigate = useNavigate()
  const search = recoveryRoute.useSearch()
  const recovery = useMutation({
    mutationFn: async (input: { email: string; code: string }) => {
      await api('/api/auth/recovery/assert', {
        method: 'POST',
        body: JSON.stringify(input),
      })
      return loadMe()
    },
    onSuccess: (me) => {
      if (openNext(search.next)) {
        return
      }

      void navigate({ to: me.ready ? '/' : '/security' })
    },
  })
  const form = useForm({
    defaultValues: { email: search.email, code: '' },
    onSubmit: ({ value }) => {
      recovery.mutate({
        email: value.email.trim().toLowerCase(),
        code: value.code.trim(),
      })
    },
  })

  return (
    <main className="guest">
      <div className="guest-card panel">
        <Page title="Having trouble signing in?">
          <form
            className="flex flex-col gap-2"
            onSubmit={(event) => {
              event.preventDefault()
              void form.handleSubmit()
            }}
          >
            <p className="subtle">Enter your email and a recovery code.</p>
            <form.Field
              name="email"
              validators={{
                onSubmit: ({ value }) => emailError(value),
              }}
            >
              {(field) => (
                <TextField
                  label="Email"
                  type="email"
                  autoComplete="username"
                  value={field.state.value}
                  onChange={field.handleChange}
                  onBlur={field.handleBlur}
                  error={fieldErrors(field.state.meta.errors)}
                />
              )}
            </form.Field>
            <form.Field
              name="code"
              validators={{
                onSubmit: ({ value }) => (value.trim() ? undefined : 'Enter a recovery code.'),
              }}
            >
              {(field) => (
                <TextField
                  label="Recovery code"
                  autoComplete="one-time-code"
                  value={field.state.value}
                  onChange={field.handleChange}
                  onBlur={field.handleBlur}
                  error={fieldErrors(field.state.meta.errors)}
                />
              )}
            </form.Field>
            <Button type="submit" isDisabled={recovery.isPending}>
              Use recovery code
            </Button>
            <ErrorText error={recovery.error} />
            <Link className="trouble-link" to="/sign-in" search={{ next: search.next }}>
              Back to sign in
            </Link>
          </form>
        </Page>
      </div>
    </main>
  )
}
