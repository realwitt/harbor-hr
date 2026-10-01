import { useForm } from '@tanstack/react-form'
import { useMutation } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { api } from '../api'
import { meQuery, queryClient } from '../query'
import type { Me } from '../types'
import { Button, ErrorText, Page, TextField, fieldErrors } from '../ui'
import { getPasskey } from '../webauthn'

async function loadMe(): Promise<Me> {
  const me = await api<Me>('/api/auth/me')
  queryClient.setQueryData(meQuery.queryKey, me)
  return me
}

export function SignInPage() {
  const navigate = useNavigate()
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
      void navigate({ to: me.ready ? '/' : '/security' })
    },
  })
  const recovery = useMutation({
    mutationFn: async (input: { email: string; code: string }) => {
      await api('/api/auth/recovery/assert', {
        method: 'POST',
        body: JSON.stringify(input),
      })
      return loadMe()
    },
    onSuccess: (me) => {
      void navigate({ to: me.ready ? '/' : '/security' })
    },
  })
  const form = useForm({
    defaultValues: { email: '', code: '' },
    onSubmit: ({ value }) => {
      passkey.mutate(value.email.trim().toLowerCase())
    },
  })

  return (
    <main className="mx-auto flex max-w-md flex-col gap-3 px-3 py-6">
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
              onSubmit: ({ value }) => (value.includes('@') ? undefined : 'Enter an email address.'),
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
          <form.Field name="code">
            {(field) => (
              <TextField
                label="Recovery code"
                autoComplete="one-time-code"
                value={field.state.value}
                onChange={field.handleChange}
                onBlur={field.handleBlur}
              />
            )}
          </form.Field>
          <Button
            quiet
            isDisabled={recovery.isPending}
            onPress={() => {
              const email = form.getFieldValue('email').trim().toLowerCase()
              const code = form.getFieldValue('code').trim()
              recovery.mutate({ email, code })
            }}
          >
            Use recovery code
          </Button>
          <ErrorText error={recovery.error} />
        </form>
      </Page>
    </main>
  )
}
