import { useForm } from '@tanstack/react-form'
import { useMutation } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { Button, ErrorText, Page, TextField, fieldErrors } from '../ui'

const turnstileSiteKey = import.meta.env.VITE_TURNSTILE_SITE_KEY ?? ''

function useTurnstileToken(siteKey: string, active: boolean) {
  const host = useRef<HTMLDivElement>(null)
  const [token, setToken] = useState('')

  useEffect(() => {
    if (!siteKey || !active) {
      return
    }

    let widgetId = ''
    let cancelled = false
    const render = () => {
      if (cancelled || widgetId || !host.current || !window.turnstile) {
        return
      }
      widgetId = window.turnstile.render(host.current, {
        sitekey: siteKey,
        callback: (value) => setToken(value),
        'error-callback': () => setToken(''),
        'expired-callback': () => setToken(''),
      })
    }

    if (window.turnstile) {
      render()
    } else {
      let script = document.querySelector<HTMLScriptElement>('script[data-turnstile]')
      if (!script) {
        script = document.createElement('script')
        script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
        script.async = true
        script.dataset.turnstile = 'true'
        document.head.appendChild(script)
      }
      script.addEventListener('load', render)
    }

    return () => {
      cancelled = true
      if (widgetId && window.turnstile) {
        window.turnstile.remove(widgetId)
      }
    }
  }, [siteKey, active])

  return { host, token }
}

export function JoinPage() {
  const turnstile = useTurnstileToken(turnstileSiteKey, true)
  const [sent, setSent] = useState<boolean | null>(null)
  const request = useMutation({
    mutationFn: (value: { name: string; email: string; note: string }) =>
      api<{ id: string; mailSent: boolean }>('/api/auth/join-requests', {
        method: 'POST',
        body: JSON.stringify({
          name: value.name.trim(),
          email: value.email.trim(),
          note: value.note.trim() || undefined,
          turnstileToken: turnstile.token || undefined,
        }),
      }),
    onSuccess: (result) => setSent(result.mailSent),
  })
  const form = useForm({
    defaultValues: { name: '', email: '', note: '' },
    onSubmit: ({ value }) => request.mutateAsync(value),
  })
  const waiting = turnstileSiteKey !== '' && turnstile.token === ''

  return (
    <main className="guest">
      <div className="guest-card panel">
        <Page title="Request to join">
          {sent === null ? (
            <form
              className="stack-form"
              noValidate
              onSubmit={(event) => {
                event.preventDefault()
                void form.handleSubmit()
              }}
            >
              <p className="subtle">Send your name and email. An admin sets up your account.</p>
              <form.Field
                name="name"
                validators={{ onSubmit: ({ value }) => (value.trim() ? undefined : 'Enter your name and email.') }}
              >
                {(field) => (
                  <TextField
                    label="Name"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onBlur={field.handleBlur}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field
                name="email"
                validators={{
                  onSubmit: ({ value }) => (value.includes('@') ? undefined : 'Enter your name and email.'),
                }}
              >
                {(field) => (
                  <TextField
                    label="Email"
                    type="email"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onBlur={field.handleBlur}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              <form.Field
                name="note"
                validators={{
                  onSubmit: ({ value }) => (value.trim().length <= 500 ? undefined : 'The note is too long.'),
                }}
              >
                {(field) => (
                  <TextField
                    label="Note"
                    value={field.state.value}
                    onChange={field.handleChange}
                    onBlur={field.handleBlur}
                    error={fieldErrors(field.state.meta.errors)}
                  />
                )}
              </form.Field>
              {turnstileSiteKey ? <div ref={turnstile.host} /> : null}
              <Button type="submit" isDisabled={request.isPending || waiting}>
                Request to join
              </Button>
              <ErrorText error={request.error} />
            </form>
          ) : (
            <p className="subtle">
              {sent
                ? 'Request sent. You get an email when your account is ready.'
                : 'Request saved. Email is not configured on this server.'}
            </p>
          )}
          <Link className="trouble-link" to="/sign-in" search={{ next: '', returnTo: '' }}>
            Back to sign in
          </Link>
        </Page>
      </div>
    </main>
  )
}
