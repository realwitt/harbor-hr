import { useMutation, useQuery } from '@tanstack/react-query'
import { useNavigate, useParams } from '@tanstack/react-router'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { meQuery, queryClient } from '../query'
import type { InviteInfo, Me } from '../types'
import { Button, ErrorText, Page } from '../ui'
import { createPasskey } from '../webauthn'

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

export function InvitePage() {
  const { token } = useParams({ from: '/invite/$token' })
  const navigate = useNavigate()
  const invite = useQuery({
    queryKey: ['invite', token],
    queryFn: () => api<InviteInfo>(`/api/auth/invites/${encodeURIComponent(token)}`),
  })
  const turnstile = useTurnstileToken(turnstileSiteKey, Boolean(invite.data))
  const register = useMutation({
    mutationFn: async () => {
      const options = await api<unknown>('/api/auth/register/options', {
        method: 'POST',
        body: JSON.stringify({ token }),
      })
      const attestation = await createPasskey(options)
      await api('/api/auth/register', {
        method: 'POST',
        body: JSON.stringify({
          token,
          attestation,
          turnstileToken: turnstile.token || undefined,
        }),
      })
      const me = await api<Me>('/api/auth/me')
      queryClient.setQueryData(meQuery.queryKey, me)
      return me
    },
    onSuccess: async (me) => {
      await navigate({ to: me.ready ? '/' : '/security' })
    },
  })

  return (
    <main className="guest">
      <div className="guest-card panel">
      <Page title="Invite">
        {invite.isPending ? <p className="text-sm">Loading the invite.</p> : null}
        <ErrorText error={invite.error} />
        {invite.data ? (
          <>
            <p className="text-sm">
              {invite.data.name} ({invite.data.email})
            </p>
            {turnstileSiteKey ? <div ref={turnstile.host} /> : null}
            <Button
              onPress={() => register.mutate()}
              isDisabled={register.isPending || (turnstileSiteKey !== '' && turnstile.token === '')}
            >
              Create passkey
            </Button>
            <ErrorText error={register.error} />
          </>
        ) : null}
      </Page>
      </div>
    </main>
  )
}
